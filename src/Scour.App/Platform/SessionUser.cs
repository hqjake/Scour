using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Scour.App.Platform;

/// <summary>The account signed in to the desktop, when Scour runs as a different one.</summary>
public sealed record SignedInUser(string Account, string Sid, string ProfileDir);

/// <summary>
/// Handles Scour running as a different account from the one signed in to the desktop. That happens on a
/// standard account: the UAC prompt asks for an administrator's password and Scour then runs as that
/// administrator. Per-user features (HKCU, AppData, browsers, startup, Store apps) are pointed at the
/// signed-in user instead, so they clean the files of the person actually using the PC.
/// </summary>
public static class SessionUser
{
    private static readonly Lazy<(string? Name, SignedInUser? User)> _detected = new(Detect);

    /// <summary>The desktop's signed-in account ("PC\Alex") when it isn't the one Scour runs as; otherwise null.</summary>
    public static string? OtherSignedInUser() => _detected.Value.Name;

    /// <summary>That account, with its profile and loaded registry hive, when Scour can work on its behalf.</summary>
    public static SignedInUser? Other => _detected.Value.User;

    private static (string?, SignedInUser?) Detect()
    {
        try
        {
            var user = Query(NativeMethods.WTSUserName);
            if (string.IsNullOrEmpty(user)) return (null, null);
            var domain = Query(NativeMethods.WTSDomainName) ?? "";
            var account = domain.Length > 0 ? $@"{domain}\{user}" : user;

            SecurityIdentifier? sid = null;
            try { sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier)); }
            catch { /* unresolvable: compare names below */ }

            using var me = WindowsIdentity.GetCurrent();
            bool same = sid is not null ? sid == me.User : string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase);
            if (same) return (null, null);
            return (account, sid is null ? null : Resolve(account, sid.Value));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>The user's profile folder and registry hive; null unless both are there (the hive is loaded while they're signed in).</summary>
    private static SignedInUser? Resolve(string account, string sid)
    {
        using var hive = Registry.Users.OpenSubKey(sid);
        if (hive is null) return null;
        using var profileKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        var profile = profileKey?.GetValue("ProfileImagePath") as string;
        if (string.IsNullOrEmpty(profile)) return null;
        profile = Environment.ExpandEnvironmentVariables(profile);
        return Directory.Exists(profile) ? new SignedInUser(account, sid, profile) : null;
    }

    private static string? Query(int infoClass)
    {
        int session = Process.GetCurrentProcess().SessionId;
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { NativeMethods.WTSFreeMemory(buffer); }
    }
}
