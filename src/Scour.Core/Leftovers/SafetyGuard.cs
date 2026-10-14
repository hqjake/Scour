using Scour.Core.Platform;

namespace Scour.Core.Leftovers;

/// <summary>
/// Last line of defence: decides which folders and registry keys may never be offered for
/// deletion, whatever the matching heuristics say.
/// </summary>
public sealed class SafetyGuard
{
    private readonly List<string> _protectedRoots;
    private readonly List<string> _protectedPaths;
    private readonly List<RegKeyPath> _protectedKeys;
    private readonly KnownFolders _folders;

    /// <summary>Registry keys that are part of Windows' own structure.</summary>
    public static readonly RegKeyPath[] StructuralKeys =
    [
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
        RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\App Paths"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\CLSID"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\WOW6432Node\CLSID"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\TypeLib"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Interface"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\AppID"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Applications"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\Products"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\Features"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\UpgradeCodes"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components"),
        RegKeyPath.CurrentUser(@"Software\Classes\CLSID"),
        RegKeyPath.CurrentUser(@"Software\Classes\Applications"),
        RegKeyPath.CurrentUser(@"Software\Microsoft\Installer\Products"),
        RegKeyPath.CurrentUser(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Clients\StartMenuInternet"),
        RegKeyPath.LocalMachine(@"SOFTWARE\Clients\Mail"),
        RegKeyPath.LocalMachine(@"SOFTWARE\RegisteredApplications"),
        RegKeyPath.CurrentUser(@"Software\RegisteredApplications"),
        RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services"),
    ];

    /// <summary>Second-level SOFTWARE keys that are never deletable as a whole.</summary>
    public static readonly HashSet<string> ProtectedSoftwareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC",
        "Khronos", "Partner", "DefaultUserEnvironment", "Setup", "AppDataLow", "System", "Software",
    };

    /// <summary>Third-level keys under SOFTWARE\Microsoft that belong to Windows itself.</summary>
    public static readonly HashSet<string> ProtectedMicrosoftNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows NT", "Cryptography", "Active Setup", "Internet Explorer", "Windows Defender", "Ole", "COM3",
        "Rpc", ".NETFramework", "NET Framework Setup", "Speech", "CTF", "Input", "InputMethod", "Shared Tools", "Fusion",
        "SystemCertificates", "Windows Search", "WindowsRuntime", "Wbem", "Tracing", "Command Processor", "Notepad",
        "Security Center", "Ink", "Multimedia", "PolicyManager", "Provisioning", "Office", "OneDrive", "Edge", "EdgeUpdate",
        "Windows Media Player NSS", "MediaPlayer", "DirectX", "DirectShow", "Direct3D", "Advanced INF Setup", "Audio",
        "Assistance", "Driver Signing", "Function Discovery", "IdentityStore", "IdentityCRL", "Lighting", "MSF",
        "Personalization", "Phone", "SQMClient", "TabletTip", "Terminal Server Client", "Touchpad", "Wisp", "WAB",
    };

    public SafetyGuard(KnownFolders folders, IEnumerable<string> protectedPaths, IEnumerable<RegKeyPath> protectedKeys)
    {
        _folders = folders;
        _protectedRoots = folders.ProtectedRoots().Select(WinPath.Normalize).ToList();
        _protectedPaths = protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(WinPath.Normalize).ToList();
        _protectedKeys = protectedKeys.ToList();
    }

    /// <summary>True if the folder (or file) must never be deleted.</summary>
    public bool IsProtectedPath(string path)
    {
        path = WinPath.Normalize(path);
        if (path.Length <= 3 || WinPath.Depth(path) < 1) return true;
        if (WinPath.IsSameOrUnder(path, _folders.WindowsDir)) return true;
        foreach (var r in _protectedRoots)
            if (WinPath.IsSameOrUnder(r, path)) return true; // path is a protected root or one of its ancestors
        return ContainsProtectedInstall(path);
    }

    /// <summary>True if another installed program lives in or below <paramref name="path"/>.</summary>
    public bool ContainsProtectedInstall(string path)
    {
        foreach (var p in _protectedPaths)
            if (WinPath.IsSameOrUnder(p, path)) return true;
        return false;
    }

    /// <summary>True if the key must never be deleted as a whole.</summary>
    public bool IsProtectedKey(RegKeyPath key)
    {
        if (key.Depth <= 1) return true;
        foreach (var s in StructuralKeys)
            if (s.IsSameOrUnder(key)) return true; // key is structural or an ancestor of one
        foreach (var k in _protectedKeys)
            if (k.IsSameOrUnder(key)) return true;

        var parts = key.SubPath.Split('\\');
        int softwareIndex = Array.FindIndex(parts, p => p.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase));
        if (softwareIndex >= 0)
        {
            int rel = parts.Length - softwareIndex - 1;
            int offset = softwareIndex + 1;
            if (rel >= 1 && parts[offset].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                offset++;
                rel--;
            }
            if (rel == 0) return true;
            if (rel == 1 && ProtectedSoftwareNames.Contains(parts[offset])) return true;
            if (rel == 2 && parts[offset].Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                ProtectedMicrosoftNames.Contains(parts[offset + 1])) return true;
        }
        if (key.Hive == RegHive.LocalMachine && key.SubPath.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            // Only individual service keys may go.
            var svc = RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services");
            return !(key.Parent is { } parent && parent.Equals(svc));
        }
        return false;
    }
}
