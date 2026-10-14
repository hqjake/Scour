using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Scour.Core.Programs;

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

    /// <summary>Runs a console tool hidden and returns (exit code, stdout+stderr).</summary>
    public static async Task<(int ExitCode, string Output)> RunHiddenAsync(string exe, string args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in time.");
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    public static Task<(int ExitCode, string Output)> PowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + script));
        return RunHiddenAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeout, ct);
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
