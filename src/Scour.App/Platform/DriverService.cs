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
        var byContent = new Lazy<Dictionary<string, string>>(() => PackagesByContent(repository));
        var byPublished = PackagesByPublishedName();
        var list = new List<DriverPackage>();
        foreach (var inf in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            ct.ThrowIfCancellationRequested();
            var published = Path.GetFileName(inf);
            InfInfo info;
            try { info = InfParser.Parse(File.ReadAllText(inf)); }
            catch (IOException) { continue; }

            var packageId = byPublished.GetValueOrDefault(published) ?? PackageId(published);
            // Last resort: Windows copies the INF into the store unchanged, so the same bytes identify the folder.
            if (packageId is null || !Directory.Exists(Path.Combine(repository, packageId)))
                packageId = byContent.Value.GetValueOrDefault(Hash(inf)) ?? packageId;
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

    /// <summary>DriverDatabase\DriverPackages\&lt;package&gt; holds the published name (oemNN.inf) as its default value.</summary>
    private static Dictionary<string, string> PackagesByPublishedName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\DriverDatabase\DriverPackages");
        if (root is null) return map;
        foreach (var id in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(id);
                if (key?.GetValue("") is string published && published.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                    map.TryAdd(published, id);
            }
            catch (System.Security.SecurityException) { /* skip */ }
        }
        return map;
    }

    private static Dictionary<string, string> PackagesByContent(string repository)
    {
        var map = new Dictionary<string, string>();
        if (!Directory.Exists(repository)) return map;
        foreach (var dir in Directory.EnumerateDirectories(repository))
        {
            try
            {
                foreach (var inf in Directory.EnumerateFiles(dir, "*.inf")) map.TryAdd(Hash(inf), Path.GetFileName(dir));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* skip */ }
        }
        return map;
    }

    private static string Hash(string file)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return file; }
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
