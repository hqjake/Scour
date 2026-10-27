using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Scour.App.Platform;

/// <summary>
/// Notices when Scour runs as a different account from the one signed in to the desktop. That happens on a
/// standard account: the UAC prompt asks for an administrator's password and Scour then runs as that
/// administrator, so the user's own browsers, temp files, startup apps and Store apps aren't the ones it sees.
/// </summary>
public static class SessionUser
{
    /// <summary>The desktop's signed-in account ("PC\Alex") when it isn't the one Scour runs as; otherwise null.</summary>
    public static string? OtherSignedInUser()
    {
        try
        {
            var user = Query(NativeMethods.WTSUserName);
            if (string.IsNullOrEmpty(user)) return null;
            var domain = Query(NativeMethods.WTSDomainName) ?? "";
            var account = domain.Length > 0 ? $@"{domain}\{user}" : user;
            bool same;
            try
            {
                using var me = WindowsIdentity.GetCurrent();
                same = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Equals(me.User);
            }
            catch
            {
                same = string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase);
            }
            return same ? null : account;
        }
        catch
        {
            return null;
        }
    }

    private static string? Query(int infoClass)
    {
        int session = Process.GetCurrentProcess().SessionId;
        if (!NativeMethods.WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { NativeMethods.WTSFreeMemory(buffer); }
    }
}
