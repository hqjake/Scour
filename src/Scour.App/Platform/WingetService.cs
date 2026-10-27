using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Scour.Core.Repair;
using Scour.Core.Updates;

namespace Scour.App.Platform;

public static class WingetService
{
    public static string? WingetPath()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* bad PATH entry */ }
        }
        // The alias above exists only for accounts that have signed in and registered App Installer — not for
        // an administrator account entered at the UAC prompt. The real exe is reachable from any admin.
        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            return Directory.EnumerateDirectories(apps, $"Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe")
                .Select(d => Path.Combine(d, "winget.exe"))
                .Where(File.Exists)
                .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))!.Split('_')[1], out var v) ? v : new Version())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static async Task<List<WingetPackage>> ListUpgradesAsync(bool includeUnknown, CancellationToken ct = default)
    {
        // Prefer the official PowerShell module: structured output, no text parsing.
        var script =
            "Import-Module Microsoft.WinGet.Client -ErrorAction Stop; " +
            "Get-WinGetPackage | Where-Object { $_.IsUpdateAvailable } | ForEach-Object { " +
            "[pscustomobject]@{ Name = $_.Name; Id = $_.Id; Version = \"$($_.InstalledVersion)\"; Available = \"$($_.AvailableVersions[0])\"; Source = \"$($_.Source)\" } } | ConvertTo-Json -Compress";
        try
        {
            var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(60), ct);
            if (result.ExitCode == 0)
            {
                var list = ParseJson(result.StdOut);
                return includeUnknown ? list : list.Where(p => !p.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* fall back to the CLI */ }

        var winget = WingetPath() ?? throw new InvalidOperationException("winget isn't installed. Install \"App Installer\" from the Microsoft Store.");
        var lines = new List<string>();
        int exit = await RunAsync(winget, "upgrade --accept-source-agreements --disable-interactivity" + (includeUnknown ? " --include-unknown" : ""),
                                  lines.Add, ct);
        var packages = WingetParser.ParseUpgradeTable(lines);
        if (packages.Count == 0 && exit != 0 && unchecked((uint)exit) != 0x8A150014)
            throw new InvalidOperationException("winget failed: " + WingetParser.DescribeExitCode(exit));
        return packages;
    }

    private static List<WingetPackage> ParseJson(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return items.Select(e => new WingetPackage(S(e, "Name"), S(e, "Id"), S(e, "Version"), S(e, "Available"), S(e, "Source"), false))
                    .Where(p => p.Id.Length > 0).ToList();
    }

    public static Task<int> UpgradeAsync(WingetPackage package, Action<string> output, CancellationToken ct)
    {
        var winget = WingetPath() ?? throw new InvalidOperationException("winget isn't installed.");
        return RunAsync(winget, WingetParser.UpgradeArguments(package), output, ct);
    }

    /// <summary>Writes every winget-installable app on this PC to a reinstall kit (winget export JSON).</summary>
    public static async Task<List<KitApp>> ExportAsync(string path, Action<string> output, CancellationToken ct)
    {
        var winget = WingetPath() ?? throw new InvalidOperationException("winget isn't installed. Install \"App Installer\" from the Microsoft Store.");
        int code = await RunAsync(winget, ReinstallKit.ExportArguments(path), output, ct);
        if (!File.Exists(path)) throw new InvalidOperationException("winget couldn't export the app list: " + WingetParser.DescribeExitCode(code));
        return ReinstallKit.Parse(await File.ReadAllTextAsync(path, ct));
    }

    public static Task<int> InstallAsync(KitApp app, Action<string> output, CancellationToken ct)
    {
        var winget = WingetPath() ?? throw new InvalidOperationException("winget isn't installed.");
        return RunAsync(winget, ReinstallKit.InstallArguments(app), output, ct);
    }

    private const string NoInteractivity = " --disable-interactivity";

    private static async Task<int> RunAsync(string exe, string args, Action<string> output, CancellationToken ct)
    {
        int code = await RunOnceAsync(exe, args, output, ct);
        // winget before 1.4 (still common on Windows 10) rejects --disable-interactivity as an unknown argument.
        if (unchecked((uint)code) == 0x8A150002 && args.Contains(NoInteractivity, StringComparison.Ordinal))
            code = await RunOnceAsync(exe, args.Replace(NoInteractivity, ""), output, ct);
        return code;
    }

    private static async Task<int> RunOnceAsync(string exe, string args, Action<string> output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start winget.");
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { /* gone */ } });
        async Task Pump(Stream s)
        {
            var decoder = new OutputDecoder();
            var buffer = new byte[4096];
            int n;
            while ((n = await s.ReadAsync(buffer)) > 0)
                foreach (var line in decoder.Feed(buffer.AsSpan(0, n))) output(line);
            foreach (var line in decoder.Flush()) output(line);
        }
        var a = Pump(p.StandardOutput.BaseStream);
        var b = Pump(p.StandardError.BaseStream);
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(a, b);
        return p.ExitCode;
    }
}
