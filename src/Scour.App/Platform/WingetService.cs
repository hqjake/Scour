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
        return null;
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
            var (code, output) = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(3), ct);
            if (code == 0)
            {
                var list = ParseJson(output);
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

    private static async Task<int> RunAsync(string exe, string args, Action<string> output, CancellationToken ct)
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
