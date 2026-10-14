using Scour.Core.Platform;

namespace Scour.Core.Tracing;

public sealed record RegistryWatch(RegKeyPath Root, int MaxDepth, bool CaptureValues, params string[] ExcludedChildren);

public sealed record FolderWatch(string Root, int MaxDepth, params string[] ExcludedChildren);

/// <summary>A point-in-time listing of watched registry keys/values and files/folders.</summary>
public sealed class Snapshot
{
    internal Dictionary<string, RegKeyPath> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, (RegKeyPath Key, string Name, string Fingerprint)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int KeyCount => Keys.Count;
    public int ValueCount => Values.Count;
    public int DirectoryCount => Directories.Count;
    public int FileCount => Files.Count;

    internal static string ValueId(RegKeyPath key, string name) => key + "|" + name;
}

/// <summary>Takes snapshots of the places installers write to.</summary>
public sealed class SnapshotTaker(IRegistry registry, IFileSystem fs, KnownFolders folders)
{
    /// <summary>Path fragments that change constantly and are never attributed to an installer.</summary>
    public static readonly string[] NoiseFragments =
    [
        @"\Cache\", @"\Code Cache\", @"\GPUCache\", @"\CrashDumps\", @"\Temp\", @"\D3DSCache\", @"\INetCache\",
        @"\Crashpad\", @"\ShaderCache\", @"\DawnCache\", @"\Service Worker\", @"\IndexedDB\", @"\Local Storage\",
        @"\Session Storage\", @"\logs\", @"\Logs\", @"\GrShaderCache\", @"\cache2\", @"\Network\",
    ];

    public List<RegistryWatch> RegistryWatches { get; } = DefaultRegistryWatches();
    public List<FolderWatch> FolderWatches { get; } = DefaultFolderWatches(folders);

    public static List<RegistryWatch> DefaultRegistryWatches() =>
    [
        new(RegKeyPath.LocalMachine("SOFTWARE"), 6, true, "Classes", "Microsoft", "WOW6432Node", "Policies"),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node"), 6, true, "Classes", "Microsoft", "Policies"),
        new(RegKeyPath.CurrentUser("Software"), 6, true, "Classes", "Microsoft", "Policies"),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), 1, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"), 1, true),
        new(RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"), 1, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"), 1, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"), 1, true),
        new(RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\App Paths"), 1, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), 0, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"), 0, true),
        new(RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"), 0, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes"), 1, false, "CLSID", "WOW6432Node", "Interface", "TypeLib"),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\CLSID"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\WOW6432Node\CLSID"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\TypeLib"), 1, false),
        new(RegKeyPath.CurrentUser(@"Software\Classes"), 1, false, "CLSID", "Local Settings"),
        new(RegKeyPath.CurrentUser(@"Software\Classes\CLSID"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\Products"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\Features"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\UpgradeCodes"), 1, false),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products"), 1, false),
        new(RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services"), 1, false),
        new(RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"), 0, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\RegisteredApplications"), 0, true),
        new(RegKeyPath.CurrentUser(@"Software\RegisteredApplications"), 0, true),
        new(RegKeyPath.LocalMachine(@"SOFTWARE\Clients\StartMenuInternet"), 1, false),
    ];

    public static List<FolderWatch> DefaultFolderWatches(KnownFolders f) =>
    [
        new(f.ProgramFiles, 4, "WindowsApps", "Windows Defender", "ModifiableWindowsApps"),
        new(f.ProgramFilesX86, 4),
        new(f.ProgramData, 4, "Microsoft", "Scour", "Packages", "USOPrivate", "USOShared"),
        new(f.RoamingAppData, 4, "Microsoft"),
        new(f.LocalAppData, 4, "Microsoft", "Temp", "Packages", "D3DSCache", "CrashDumps", "ConnectedDevicesPlatform"),
        new(f.LocalLowAppData, 3, "Microsoft"),
        new(f.UserStartMenuPrograms, 3),
        new(f.CommonStartMenuPrograms, 3),
        new(f.UserDesktop, 0),
        new(f.CommonDesktop, 0),
    ];

    public Snapshot Take(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var snap = new Snapshot();
        foreach (var w in RegistryWatches)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {w.Root}");
            WalkKey(snap, w, w.Root, 0, ct);
        }
        foreach (var w in FolderWatches)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {w.Root}");
            if (fs.DirectoryExists(w.Root)) WalkFolder(snap, w, w.Root, 0, ct);
        }
        return snap;
    }

    private void WalkKey(Snapshot snap, RegistryWatch w, RegKeyPath key, int depth, CancellationToken ct)
    {
        snap.Keys[key.ToString()] = key;
        if (w.CaptureValues)
            foreach (var name in registry.GetValueNames(key))
                if (registry.GetValue(key, name) is { } v)
                    snap.Values[Snapshot.ValueId(key, name)] = (key, name, v.Fingerprint());
        if (depth >= w.MaxDepth) return;
        foreach (var child in registry.GetSubKeyNames(key))
        {
            if (depth == 0 && w.ExcludedChildren.Contains(child, StringComparer.OrdinalIgnoreCase)) continue;
            if (snap.Keys.Count % 5000 == 0) ct.ThrowIfCancellationRequested();
            WalkKey(snap, w, key.Child(child), depth + 1, ct);
        }
    }

    private void WalkFolder(Snapshot snap, FolderWatch w, string dir, int depth, CancellationToken ct)
    {
        snap.Directories[dir] = dir;
        foreach (var f in fs.EnumerateFiles(dir)) snap.Files[f] = f;
        if (depth >= w.MaxDepth) return;
        foreach (var sub in fs.EnumerateDirectories(dir))
        {
            if (depth == 0 && w.ExcludedChildren.Contains(WinPath.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) continue;
            if (snap.Directories.Count % 2000 == 0) ct.ThrowIfCancellationRequested();
            WalkFolder(snap, w, sub, depth + 1, ct);
        }
    }

    public static bool IsNoise(string path, IEnumerable<string> extraIgnored)
    {
        foreach (var n in NoiseFragments)
            if (path.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ignored in extraIgnored)
            if (WinPath.IsSameOrUnder(path, ignored)) return true;
        return false;
    }
}
