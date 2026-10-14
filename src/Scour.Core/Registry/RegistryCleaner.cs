using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Shortcuts;

namespace Scour.Core.Registry;

public enum RegistryIssueCategory
{
    OrphanedUninstallEntries,
    BrokenAppPaths,
    BrokenStartupEntries,
    BrokenFileAssociations,
    ObsoleteShellCache,
    ObsoleteCompatibilityEntries,
    FirewallRulesForMissingPrograms,
    BrokenShortcuts,
}

public sealed record RegistryCategoryInfo(RegistryIssueCategory Category, string Name, string Description);

public sealed class RegistryIssue
{
    public required RegistryIssueCategory Category { get; init; }
    public required LeftoverItem Item { get; init; }
    public required string Problem { get; init; }
}

/// <summary>
/// Finds registry entries (and shortcuts) that point at files which no longer exist. Only
/// categories where a dead reference is unambiguous are included; anything that can't be
/// verified (system folders, drives that aren't connected, bare program names) is left alone.
/// </summary>
public sealed class RegistryCleaner(IRegistry registry, IFileSystem fs, KnownFolders folders)
{
    public static readonly IReadOnlyList<RegistryCategoryInfo> Categories =
    [
        new(RegistryIssueCategory.OrphanedUninstallEntries, "Uninstall entries of removed programs",
            "Programs listed as installed whose uninstaller and files are gone."),
        new(RegistryIssueCategory.BrokenAppPaths, "Broken App Paths", "Program shortcuts for the Run box (Win+R) that point at missing files."),
        new(RegistryIssueCategory.BrokenStartupEntries, "Startup entries for missing programs", "Items that try to start a program that no longer exists at sign-in."),
        new(RegistryIssueCategory.BrokenFileAssociations, "Broken file associations", "File types and \"Open with\" entries for programs that are gone."),
        new(RegistryIssueCategory.ObsoleteShellCache, "Obsolete program name cache", "Explorer's cached names (MUI cache) for programs that no longer exist."),
        new(RegistryIssueCategory.ObsoleteCompatibilityEntries, "Obsolete compatibility entries", "Compatibility settings and history for deleted programs."),
        new(RegistryIssueCategory.FirewallRulesForMissingPrograms, "Firewall rules for missing programs", "Windows Firewall rules for programs that are no longer on disk."),
        new(RegistryIssueCategory.BrokenShortcuts, "Broken shortcuts", "Start menu and desktop shortcuts whose target is missing."),
    ];

    private static readonly HashSet<string> ClassesSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "TypeLib", "Interface", "AppID", "WOW6432Node", "Installer", "Local Settings", "Applications", "Directory", "Drive",
        "Folder", "*", "AllFilesystemObjects", "SystemFileAssociations", "MIME", "Component Categories", "exefile", "batfile",
        "cmdfile", "comfile", "lnkfile", "dllfile", "Unknown", "http", "https", "ftp", "mailto",
    };

    public List<RegistryIssue> Scan(IReadOnlySet<RegistryIssueCategory> categories, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var issues = new List<RegistryIssue>();
        void Step(RegistryIssueCategory c, Action<List<RegistryIssue>> scan)
        {
            if (!categories.Contains(c)) return;
            ct.ThrowIfCancellationRequested();
            progress?.Report(Categories.First(x => x.Category == c).Name);
            scan(issues);
        }
        Step(RegistryIssueCategory.OrphanedUninstallEntries, ScanUninstall);
        Step(RegistryIssueCategory.BrokenAppPaths, ScanAppPaths);
        Step(RegistryIssueCategory.BrokenStartupEntries, ScanStartup);
        Step(RegistryIssueCategory.BrokenFileAssociations, l => ScanAssociations(l, ct));
        Step(RegistryIssueCategory.ObsoleteShellCache, ScanMuiCache);
        Step(RegistryIssueCategory.ObsoleteCompatibilityEntries, ScanAppCompat);
        Step(RegistryIssueCategory.FirewallRulesForMissingPrograms, ScanFirewall);
        Step(RegistryIssueCategory.BrokenShortcuts, ScanShortcuts);
        return issues;
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// True only when <paramref name="pathOrCommand"/> names a file on a present, non-system
    /// location that definitely doesn't exist.
    /// </summary>
    public bool IsDefinitelyMissing(string? pathOrCommand)
    {
        var target = ResolveTarget(pathOrCommand);
        if (target is null) return false;
        if (WinPath.IsSameOrUnder(target, folders.WindowsDir) || IsWindowsComponentFolder(target)) return false;
        if (target.StartsWith(@"\\", StringComparison.Ordinal)) return false; // network share
        var root = target[..3];
        if (!fs.DirectoryExists(root)) return false; // drive not connected
        return !fs.FileExists(target) && !fs.DirectoryExists(target);
    }

    /// <summary>"C:\Program Files\Windows NT\…", "…\Windows Defender\…": owned by Windows, often redirected.</summary>
    private bool IsWindowsComponentFolder(string path)
    {
        foreach (var pf in new[] { folders.ProgramFiles, folders.ProgramFilesX86 })
        {
            if (!WinPath.IsStrictlyUnder(path, pf)) continue;
            var first = path[(pf.TrimEnd('\\').Length + 1)..].Split('\\')[0];
            if (first.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private string? ResolveTarget(string? pathOrCommand)
    {
        if (string.IsNullOrWhiteSpace(pathOrCommand)) return null;
        var text = folders.Expand(pathOrCommand.Trim());
        var (exe, args) = CommandLine.Split(text);
        var name = WinPath.GetFileName(exe).ToLowerInvariant();
        if (name is "rundll32.exe" or "rundll32" or "msiexec.exe" or "cmd.exe" or "wscript.exe" or "cscript.exe" or "powershell.exe" or "explorer.exe")
        {
            var inner = CommandLine.ExtractPaths(args).FirstOrDefault();
            if (inner is null) return null;
            int comma = inner.IndexOf(',');
            exe = comma > 0 ? inner[..comma] : inner;
        }
        exe = WinPath.Normalize(exe);
        if (exe.Contains('%')) return null; // unresolved variable
        return WinPath.IsRooted(exe) && !exe.StartsWith(@"\\", StringComparison.Ordinal) ? exe : null;
    }

    private static RegistryIssue KeyIssue(RegistryIssueCategory c, RegKeyPath key, string problem) => new()
    {
        Category = c, Problem = problem, Item = LeftoverItem.ForKey(key, Confidence.Safe, problem),
    };

    private RegistryIssue ValueIssue(RegistryIssueCategory c, RegKeyPath key, string name, string problem) => new()
    {
        Category = c, Problem = problem, Item = LeftoverItem.ForValue(key, name, registry.GetString(key, name), Confidence.Safe, problem),
    };

    // ---------------------------------------------------------------- categories

    private void ScanUninstall(List<RegistryIssue> issues)
    {
        foreach (var root in ProgramInventory.UninstallRoots)
            foreach (var name in registry.GetSubKeyNames(root))
            {
                var key = root.Child(name);
                var displayName = registry.GetString(key, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                var uninstall = registry.GetString(key, "UninstallString");

                if (MsiGuid.TryNormalize(name, out var code) && (registry.GetInteger(key, "WindowsInstaller") == 1 ||
                                                                   uninstall?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var packed = MsiGuid.Pack(code);
                    bool known = registry.KeyExists(RegKeyPath.LocalMachine($@"SOFTWARE\Classes\Installer\Products\{packed}")) ||
                                 registry.KeyExists(RegKeyPath.CurrentUser($@"Software\Microsoft\Installer\Products\{packed}")) ||
                                 registry.KeyExists(RegKeyPath.LocalMachine($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}"));
                    if (!known)
                        issues.Add(KeyIssue(RegistryIssueCategory.OrphanedUninstallEntries, key,
                            $"\"{displayName}\" is listed, but Windows Installer no longer knows the product"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(uninstall) || !IsDefinitelyMissing(uninstall)) continue;
                var location = registry.GetString(key, "InstallLocation");
                if (!string.IsNullOrWhiteSpace(location) && fs.DirectoryExists(folders.Expand(location.Trim().Trim('"')))) continue;
                issues.Add(KeyIssue(RegistryIssueCategory.OrphanedUninstallEntries, key, $"\"{displayName}\" is listed, but its uninstaller and files are gone"));
            }
    }

    private void ScanAppPaths(List<RegistryIssue> issues)
    {
        RegKeyPath[] roots =
        [
            RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
            RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
            RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\App Paths"),
        ];
        foreach (var root in roots)
            foreach (var name in registry.GetSubKeyNames(root))
            {
                var key = root.Child(name);
                var target = registry.GetString(key, "");
                if (IsDefinitelyMissing(target))
                    issues.Add(KeyIssue(RegistryIssueCategory.BrokenAppPaths, key, $"{name} points to a missing file"));
            }
    }

    private void ScanStartup(List<RegistryIssue> issues)
    {
        (RegKeyPath Run, RegKeyPath? Approved)[] keys =
        [
            (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
             RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")),
            (RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
             RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32")),
            (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"),
             RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")),
            (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"), null),
            (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"), null),
        ];
        foreach (var (run, approved) in keys)
            foreach (var name in registry.GetValueNames(run))
            {
                if (name.Length == 0 || !IsDefinitelyMissing(registry.GetString(run, name))) continue;
                issues.Add(ValueIssue(RegistryIssueCategory.BrokenStartupEntries, run, name, $"\"{name}\" starts a missing program"));
                if (approved is { } a && registry.ValueExists(a, name))
                    issues.Add(ValueIssue(RegistryIssueCategory.BrokenStartupEntries, a, name, $"Startup state of \"{name}\", which starts a missing program"));
            }
    }

    private void ScanAssociations(List<RegistryIssue> issues, CancellationToken ct)
    {
        RegKeyPath[] roots = [RegKeyPath.LocalMachine(@"SOFTWARE\Classes"), RegKeyPath.CurrentUser(@"Software\Classes")];
        var existingProgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var brokenProgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new List<RegKeyPath>();

        foreach (var root in roots)
        {
            foreach (var name in registry.GetSubKeyNames(root))
            {
                ct.ThrowIfCancellationRequested();
                if (name.StartsWith('.')) { extensions.Add(root.Child(name)); continue; }
                existingProgIds.Add(name);
                if (ClassesSkip.Contains(name)) continue;
                var key = root.Child(name);
                var command = registry.GetString(key.Child(@"shell\open\command"), "");
                if (command is null || registry.ValueExists(key.Child(@"shell\open\command"), "DelegateExecute")) continue;
                if (IsDefinitelyMissing(command))
                {
                    issues.Add(KeyIssue(RegistryIssueCategory.BrokenFileAssociations, key, $"File type \"{name}\" opens with a missing program"));
                    brokenProgIds.Add(name);
                }
            }

            var apps = root.Child("Applications");
            foreach (var exe in registry.GetSubKeyNames(apps))
            {
                var key = apps.Child(exe);
                if (IsDefinitelyMissing(registry.GetString(key.Child(@"shell\open\command"), "")))
                    issues.Add(KeyIssue(RegistryIssueCategory.BrokenFileAssociations, key, $"\"Open with\" entry for missing program {exe}"));
            }
        }

        foreach (var ext in extensions)
        {
            var owp = ext.Child("OpenWithProgids");
            foreach (var progId in registry.GetValueNames(owp))
            {
                if (progId.Length == 0) continue;
                // AppX associations (AppX…) are resolved elsewhere; leave them.
                if (progId.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)) continue;
                if (brokenProgIds.Contains(progId) || !existingProgIds.Contains(progId))
                    issues.Add(ValueIssue(RegistryIssueCategory.BrokenFileAssociations, owp, progId, $"{ext.Name} can be opened with \"{progId}\", which no longer exists"));
            }
        }
    }

    private void ScanMuiCache(List<RegistryIssue> issues)
    {
        var key = RegKeyPath.CurrentUser(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
        foreach (var name in registry.GetValueNames(key))
        {
            int suffix = name.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
            var path = suffix > 0 ? name[..(suffix + 4)] : name;
            if (IsDefinitelyMissing(path))
                issues.Add(ValueIssue(RegistryIssueCategory.ObsoleteShellCache, key, name, $"Cached name of missing program {WinPath.GetFileName(path)}"));
        }
    }

    private void ScanAppCompat(List<RegistryIssue> issues)
    {
        RegKeyPath[] keys =
        [
            RegKeyPath.CurrentUser(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"),
            RegKeyPath.CurrentUser(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
            RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
        ];
        foreach (var key in keys)
            foreach (var name in registry.GetValueNames(key))
                if (IsDefinitelyMissing(name))
                    issues.Add(ValueIssue(RegistryIssueCategory.ObsoleteCompatibilityEntries, key, name,
                        $"Compatibility entry for missing program {WinPath.GetFileName(name)}"));
    }

    private void ScanFirewall(List<RegistryIssue> issues)
    {
        var key = RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
        foreach (var name in registry.GetValueNames(key))
        {
            var data = registry.GetString(key, name);
            if (data is null) continue;
            int app = data.IndexOf("|App=", StringComparison.OrdinalIgnoreCase);
            if (app < 0) continue;
            int end = data.IndexOf('|', app + 5);
            var path = end < 0 ? data[(app + 5)..] : data[(app + 5)..end];
            if (path.Equals("System", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDefinitelyMissing(path))
            {
                int n = data.IndexOf("|Name=", StringComparison.OrdinalIgnoreCase);
                var ruleName = n < 0 ? name : data[(n + 6)..].Split('|')[0];
                issues.Add(ValueIssue(RegistryIssueCategory.FirewallRulesForMissingPrograms, key, name,
                    $"Firewall rule \"{ruleName}\" for missing program {WinPath.GetFileName(path)}"));
            }
        }
    }

    private void ScanShortcuts(List<RegistryIssue> issues)
    {
        var dirs = new[] { (folders.UserStartMenuPrograms, 4), (folders.CommonStartMenuPrograms, 4), (folders.UserDesktop, 0), (folders.CommonDesktop, 0) };
        foreach (var (root, depth) in dirs)
        {
            if (!fs.DirectoryExists(root)) continue;
            foreach (var file in Files(root, depth).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            {
                LnkInfo? info;
                try { info = LnkParser.TryParse(fs.ReadAllBytes(file)); }
                catch { continue; }
                var target = info?.EnvironmentTarget ?? info?.TargetPath;
                if (target is null || !IsDefinitelyMissing(target)) continue;
                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.BrokenShortcuts,
                    Problem = $"Shortcut to missing {WinPath.GetFileName(target)}",
                    Item = LeftoverItem.ForFile(file, Confidence.Safe, $"Shortcut target {target} is missing"),
                });
            }
        }
    }

    private IEnumerable<string> Files(string dir, int depth)
    {
        foreach (var f in fs.EnumerateFiles(dir)) yield return f;
        if (depth <= 0) yield break;
        foreach (var d in fs.EnumerateDirectories(dir))
            foreach (var f in Files(d, depth - 1)) yield return f;
    }
}
