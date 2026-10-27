using Scour.Core.Platform;

namespace Scour.Core.Privacy;

public enum Capability
{
    Camera,
    Microphone,
    Location,
}

/// <summary>One app's use of the camera, microphone or location, as Windows records it.</summary>
/// <param name="Id">Package family name (Store apps) or full path (desktop apps).</param>
/// <param name="Allowed">For Store apps, their own switch; for desktop apps, the shared "desktop apps" switch.</param>
public sealed record DeviceUse(Capability Capability, string Id, bool IsDesktopApp, DateTime? LastStartUtc, DateTime? LastStopUtc, bool Allowed)
{
    /// <summary>Started and not stopped: using it right now.</summary>
    public bool InUse => LastStartUtc is not null && (LastStopUtc is null || LastStopUtc < LastStartUtc);

    public DateTime? LastUsedUtc => InUse ? DateTime.UtcNow : LastStopUtc ?? LastStartUtc;
}

/// <summary>The switches for one capability: the whole thing, and desktop apps as a group.</summary>
public sealed record CapabilitySwitches(Capability Capability, bool Allowed, bool DesktopAppsAllowed, bool BlockedByPolicy);

/// <summary>
/// Which apps used the camera, microphone and location and when, read from the record Windows keeps for its
/// privacy settings (CapabilityAccessManager). Switching access off writes the same values the Settings app does,
/// so it shows there too and can be switched back from either place.
/// </summary>
public sealed class DeviceAccess(IRegistry registry)
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public static string KeyName(Capability c) => c switch
    {
        Capability.Camera => "webcam",
        Capability.Microphone => "microphone",
        _ => "location",
    };

    private static RegKeyPath UserKey(Capability c) => RegKeyPath.CurrentUser($@"{Store}\{KeyName(c)}");
    private static RegKeyPath MachineKey(Capability c) => RegKeyPath.LocalMachine($@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{KeyName(c)}");

    public CapabilitySwitches Switches(Capability c) => new(
        c,
        !IsDeny(UserKey(c)),
        !IsDeny(UserKey(c).Child("NonPackaged")),
        IsDeny(MachineKey(c)));

    public List<DeviceUse> Uses(Capability c)
    {
        var list = new List<DeviceUse>();
        var root = UserKey(c);
        foreach (var name in registry.GetSubKeyNames(root))
        {
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            var key = root.Child(name);
            list.Add(new DeviceUse(c, name, false, Time(key, "LastUsedTimeStart"), Time(key, "LastUsedTimeStop"), !IsDeny(key)));
        }
        var desktop = root.Child("NonPackaged");
        bool desktopAllowed = !IsDeny(desktop);
        foreach (var name in registry.GetSubKeyNames(desktop))
        {
            var key = desktop.Child(name);
            // "C:#Program Files#App#app.exe": backslashes are stored as '#'.
            list.Add(new DeviceUse(c, name.Replace('#', '\\'), true, Time(key, "LastUsedTimeStart"), Time(key, "LastUsedTimeStop"), desktopAllowed));
        }
        return list.Where(u => u.LastStartUtc is not null || u.LastStopUtc is not null)
                   .OrderByDescending(u => u.InUse).ThenByDescending(u => u.LastUsedUtc).ToList();
    }

    /// <summary>Allows or blocks one Store app.</summary>
    public void SetAppAllowed(Capability c, string packageFamily, bool allowed) =>
        registry.SetValue(UserKey(c).Child(packageFamily), "Value", RegValue.String(allowed ? "Allow" : "Deny"));

    /// <summary>Windows can't block desktop apps one by one, only all together.</summary>
    public void SetDesktopAppsAllowed(Capability c, bool allowed) =>
        registry.SetValue(UserKey(c).Child("NonPackaged"), "Value", RegValue.String(allowed ? "Allow" : "Deny"));

    public void SetAllowed(Capability c, bool allowed) =>
        registry.SetValue(UserKey(c), "Value", RegValue.String(allowed ? "Allow" : "Deny"));

    private bool IsDeny(RegKeyPath key) => string.Equals(registry.GetString(key, "Value"), "Deny", StringComparison.OrdinalIgnoreCase);

    private DateTime? Time(RegKeyPath key, string name)
    {
        var v = registry.GetValue(key, name);
        long ft = v?.Data switch
        {
            ulong u => unchecked((long)u),
            long l => l,
            _ => 0,
        };
        // 0 means "never"; anything outside a sane range is junk.
        if (ft <= 0) return null;
        try
        {
            var t = DateTime.FromFileTimeUtc(ft);
            return t.Year is >= 2010 and <= 2200 ? t : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
