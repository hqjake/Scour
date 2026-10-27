using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Scour.Core.Programs;
using Scour.Core.Repair;

namespace Scour.App.Platform;

public static class ProcessRunner
{
    /// <summary>
    /// Starts an uninstaller and waits until it <em>and every process it spawned</em> have exited.
    /// Inno Setup and NSIS uninstallers copy themselves to %TEMP%, relaunch and exit immediately,
    /// so waiting for the first process alone would be useless.
    /// </summary>
    public static async Task<int?> RunUninstallerAsync(UninstallCommand command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command.Executable, command.Arguments) { UseShellExecute = true };
        var dir = Path.GetDirectoryName(command.Executable);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;

        using var root = Process.Start(psi) ?? throw new InvalidOperationException("The uninstaller did not start.");
        var started = DateTime.Now.AddSeconds(-2);
        var tracked = new HashSet<uint> { (uint)root.Id };
        int idlePolls = 0;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(400, ct).ContinueWith(_ => { });
            var snapshot = Snapshot();
            bool grew;
            do
            {
                grew = false;
                foreach (var (pid, parent) in snapshot)
                {
                    if (tracked.Contains(pid) || !tracked.Contains(parent)) continue;
                    if (StartTime(pid) is { } st && st < started) continue; // PID reuse
                    tracked.Add(pid);
                    grew = true;
                }
            } while (grew);

            bool anyAlive = tracked.Any(pid => snapshot.ContainsKey(pid));
            idlePolls = anyAlive ? 0 : idlePolls + 1;
            if (idlePolls >= 3) break; // give relaunching uninstallers a moment to spawn their child
        }

        try { return root.HasExited ? root.ExitCode : null; }
        catch { return null; }
    }

    /// <summary>
    /// The console code page Windows tools (sc, netsh, dism, vssadmin, pnputil…) write in when redirected.
    /// On a German PC that's cp850, on a Russian one cp866 — reading it as UTF-8 garbles every message.
    /// </summary>
    public static Encoding OemEncoding => _oem.Value;

    private static readonly Lazy<Encoding> _oem = new(() => CodePage(NativeMethods.GetOEMCP()));

    /// <summary>The system ANSI code page, used by old file formats (.lnk strings, INF files).</summary>
    public static Encoding AnsiEncoding => _ansi.Value;

    private static readonly Lazy<Encoding> _ansi = new(() => CodePage(NativeMethods.GetACP()));

    private static Encoding CodePage(uint codePage)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)codePage);
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>
    /// Full path of a Windows tool. Bare names are looked up through PATH, which installers and "PATH
    /// cleaner" tools regularly break — and the app folder comes first, so a stray dism.exe in Downloads
    /// would win. System tools are always taken from System32.
    /// </summary>
    public static string ToolPath(string exe)
    {
        if (Path.IsPathRooted(exe)) return exe;
        if (string.Equals(exe, "powershell.exe", StringComparison.OrdinalIgnoreCase))
            exe = @"WindowsPowerShell\v1.0\powershell.exe";
        var full = Path.Combine(Environment.SystemDirectory, exe);
        return File.Exists(full) ? full : exe;
    }

    /// <summary>Runs a console tool hidden. Deconstructs to (exit code, stdout+stderr).</summary>
    public static async Task<ToolResult> RunHiddenAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ToolPath(exe), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = ReadAllAsync(p.StandardOutput.BaseStream);
        var stderr = ReadAllAsync(p.StandardError.BaseStream);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in time.");
        }
        return new ToolResult(p.ExitCode,
            OutputDecoder.Decode(await stdout, OemEncoding),
            OutputDecoder.Decode(await stderr, OemEncoding));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Runs a PowerShell script. Its stdout is UTF-8; parse <see cref="ToolResult.StdOut"/> (errors and
    /// warnings go to stderr, so they can't corrupt JSON output).
    /// </summary>
    public static async Task<ToolResult> PowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " +
                 "$WarningPreference = 'SilentlyContinue'; $InformationPreference = 'SilentlyContinue'\n" + script;

        // A script file rather than -EncodedCommand: antivirus products (and Defender's "obfuscated script" rule)
        // block hidden encoded commands from unsigned programs, which made every PowerShell-based page fail.
        // -Command exits 1 when the last statement failed; a script file doesn't unless told to.
        if (WriteScript(script + "\nif (-not $?) { exit 1 }") is { } file)
        {
            try
            {
                var result = await RunHiddenAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"", timeout, ct);
                // A Group Policy execution policy (AllSigned) overrides Bypass for files but not for commands.
                if (!result.StdErr.Contains("UnauthorizedAccess", StringComparison.Ordinal) && !result.StdErr.Contains("PSSecurityException", StringComparison.Ordinal))
                    return result;
            }
            finally
            {
                try { File.Delete(file); } catch { /* removed with the folder's next use */ }
            }
        }
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunHiddenAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeout, ct);
    }

    /// <summary>
    /// Saves a script where only administrators can write (so nobody can swap it before it runs elevated).
    /// Returns null if that isn't possible.
    /// </summary>
    private static string? WriteScript(string script)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scour", "Scripts"));
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null), System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            if (!dir.Exists) dir.Create(security);
            dir.SetAccessControl(security);
            var file = Path.Combine(dir.FullName, $"run-{Guid.NewGuid():N}.ps1");
            // With a BOM: Windows PowerShell reads BOM-less scripts in the ANSI code page.
            File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<uint, uint> Snapshot()
    {
        var result = new Dictionary<uint, uint>();
        var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            if (!NativeMethods.Process32FirstW(snap, ref entry)) return result;
            do { result[entry.th32ProcessID] = entry.th32ParentProcessID; }
            while (NativeMethods.Process32NextW(snap, ref entry));
        }
        finally { NativeMethods.CloseHandle(snap); }
        return result;
    }

    private static DateTime? StartTime(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return NativeMethods.GetProcessTimes(h, out var creation, out _, out _, out _)
                ? DateTime.FromFileTime(creation.ToLong())
                : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    public static string? GetProcessPath(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }
}

/// <summary>A console tool's exit code and output.</summary>
public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Everything the tool printed, stdout first.</summary>
    public string Output => StdErr.Length == 0 ? StdOut : StdOut.Length == 0 ? StdErr : StdOut.TrimEnd() + "\n" + StdErr;

    public void Deconstruct(out int exitCode, out string output)
    {
        exitCode = ExitCode;
        output = Output;
    }
}
