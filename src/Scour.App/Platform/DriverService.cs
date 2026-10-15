using System.IO;
using System.Management;
using Microsoft.Win32;
using Scour.Core.Drivers;

namespace Scour.App.Platform;

public static class DriverService
{
    /// <summary>Every third-party driver package (oemNN.inf) with the devices using it and its size in the driver store.</summary>
    public static List<DriverPackage> List(CancellationToken ct = default)
    {
        var devices = DevicesByInf();
        var infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var repository = Path.Combine(Environment.SystemDirectory, @"DriverStore\FileRepository");
        var list = new List<DriverPackage>();
        foreach (var inf in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            ct.ThrowIfCancellationRequested();
            var published = Path.GetFileName(inf);
            InfInfo info;
            try { info = InfParser.Parse(File.ReadAllText(inf)); }
            catch (IOException) { continue; }

            var packageId = PackageId(published);
            long size = 0;
            if (packageId is not null)
            {
                var folder = Path.Combine(repository, packageId);
                try
                {
                    if (Directory.Exists(folder))
                        size = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* partial size */ }
            }
            list.Add(new DriverPackage(published, packageId is null ? published : DriverStore.OriginalName(packageId), info, size,
                                       devices.GetValueOrDefault(published) ?? []));
        }
        return list;
    }

    /// <summary>The driver store folder name for an oemNN.inf, from Windows' driver database.</summary>
    private static string? PackageId(string publishedName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\DriverDatabase\DriverInfFiles\{publishedName}");
        if (key is null) return null;
        if (key.GetValue("Active") is string active && active.Length > 0) return active;
        return key.GetValue("") is string[] { Length: > 0 } ids ? ids[^1] : null;
    }

    private static Dictionary<string, List<string>> DevicesByInf()
    {
        // No try/catch: if Windows can't say which drivers are in use, nothing may be called "old".
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT InfName, DeviceName FROM Win32_PnPSignedDriver");
        foreach (var o in searcher.Get())
        {
            using (o)
            {
                if (o["InfName"] is not string inf || inf.Length == 0) continue;
                var name = o["DeviceName"] as string ?? "Unnamed device";
                if (!map.TryGetValue(inf, out var list)) map[inf] = list = [];
                if (!list.Contains(name)) list.Add(name);
            }
        }
        return map;
    }

    public static Task<(int ExitCode, string Output)> PnpUtilAsync(string args, TimeSpan timeout, CancellationToken ct = default) =>
        ProcessRunner.RunHiddenAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), args, timeout, ct);
}
