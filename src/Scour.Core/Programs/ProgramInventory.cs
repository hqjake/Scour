using System.Globalization;
using System.Text.RegularExpressions;
using Scour.Core.Platform;

namespace Scour.Core.Programs;

public sealed class InventoryOptions
{
    public bool IncludeSystemComponents { get; init; }
    public bool IncludeUpdates { get; init; }
}

/// <summary>Reads installed programs from the Windows "Uninstall" registry keys.</summary>
public sealed class ProgramInventory(IRegistry registry, KnownFolders folders)
{
    public static readonly RegKeyPath[] UninstallRoots =
    [
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.CurrentUser(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    private static readonly Regex KbNumber = new(@"\bKB\d{6,}\b", RegexOptions.Compiled);

    /// <summary>Leaf folder names that are just a sub-folder of the real install dir.</summary>
    private static readonly HashSet<string> GenericLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "bin32", "x64", "x86", "app", "application", "current", "program", "uninstall", "uninst", "uninstaller",
    };

    public List<InstalledProgram> GetPrograms(InventoryOptions? options = null)
    {
        options ??= new InventoryOptions();
        var result = new List<InstalledProgram>();
        foreach (var root in UninstallRoots)
        {
            foreach (var name in registry.GetSubKeyNames(root))
            {
                var p = TryRead(root.Child(name));
                if (p is null) continue;
                if (p.IsSystemComponent && !options.IncludeSystemComponents) continue;
                if (p.IsUpdate && !options.IncludeUpdates) continue;
                result.Add(p);
            }
        }
        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    public InstalledProgram? TryRead(RegKeyPath key)
    {
        var displayName = registry.GetString(key, "DisplayName")?.Trim();
        if (string.IsNullOrEmpty(displayName)) return null;

        string? Str(string n)
        {
            var v = registry.GetString(key, n)?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        var uninstall = Str("UninstallString");
        var icon = Str("DisplayIcon");
        bool windowsInstaller = registry.GetInteger(key, "WindowsInstaller") == 1;
        string? productCode = null;
        if (MsiGuid.TryNormalize(key.Name, out var g) &&
            (windowsInstaller || uninstall?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true))
            productCode = g;

        var releaseType = Str("ReleaseType");
        bool isUpdate = Str("ParentKeyName") is not null ||
                        releaseType is "Update" or "Hotfix" or "Security Update" or "Service Pack" ||
                        KbNumber.IsMatch(displayName) && displayName.Contains("Update", StringComparison.OrdinalIgnoreCase);

        var (location, inferred) = ResolveInstallLocation(Str("InstallLocation"), icon, uninstall);

        long? size = registry.GetInteger(key, "EstimatedSize") is { } kb and > 0 ? kb * 1024 : null;

        return new InstalledProgram
        {
            RegistryKey = key,
            DisplayName = displayName,
            DisplayVersion = Str("DisplayVersion"),
            Publisher = Str("Publisher"),
            InstallDate = ParseInstallDate(Str("InstallDate")),
            InstallLocation = location,
            InstallLocationInferred = inferred,
            UninstallString = uninstall,
            QuietUninstallString = Str("QuietUninstallString"),
            ModifyPath = Str("ModifyPath"),
            DisplayIcon = icon,
            EstimatedSizeBytes = size,
            IsMsi = windowsInstaller || productCode is not null,
            ProductCode = productCode,
            Is32BitEntry = key.SubPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase),
            Scope = key.Hive == RegHive.CurrentUser ? InstallScope.User : InstallScope.Machine,
            IsSystemComponent = registry.GetInteger(key, "SystemComponent") == 1,
            IsUpdate = isUpdate,
            NoRemove = registry.GetInteger(key, "NoRemove") == 1,
            HelpLink = Str("HelpLink"),
            UrlInfoAbout = Str("URLInfoAbout"),
            Comments = Str("Comments"),
            Contact = Str("Contact"),
        };
    }

    public static DateTime? ParseInstallDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d.M.yyyy"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private (string? Location, bool Inferred) ResolveInstallLocation(string? declared, string? icon, string? uninstall)
    {
        if (declared is not null)
        {
            var d = WinPath.Normalize(folders.Expand(declared));
            if (WinPath.IsRooted(d) && !IsUnsafeInstallDir(d)) return (ClimbGenericLeaf(d), false);
        }

        foreach (var candidate in new[] { icon is null ? null : CommandLine.ParseIconLocation(icon).Path,
                                          uninstall is null ? null : CommandLine.Split(uninstall).Executable })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var exe = WinPath.Normalize(folders.Expand(candidate));
            if (!WinPath.IsRooted(exe)) continue;
            var dir = WinPath.GetDirectoryName(exe);
            if (dir is null || IsUnsafeInstallDir(dir)) continue;
            return (ClimbGenericLeaf(dir), true);
        }
        return (null, false);
    }

    private string ClimbGenericLeaf(string dir)
    {
        while (GenericLeafNames.Contains(WinPath.GetFileName(dir)))
        {
            var parent = WinPath.GetDirectoryName(dir);
            if (parent is null || IsUnsafeInstallDir(parent)) break;
            dir = parent;
        }
        return dir;
    }

    /// <summary>Folders that can't be a program's own install dir (system dirs, installer caches, shared roots).</summary>
    internal bool IsUnsafeInstallDir(string dir)
    {
        if (WinPath.Depth(dir) < 1) return true;
        if (WinPath.IsSameOrUnder(dir, folders.WindowsDir)) return true;
        if (WinPath.IsSameOrUnder(dir, WinPath.Combine(folders.ProgramData, "Package Cache"))) return true;
        if (WinPath.IsSameOrUnder(dir, folders.UserTemp)) return true;
        foreach (var root in folders.ProtectedRoots())
            if (WinPath.AreSame(dir, root)) return true;
        return false;
    }
}
