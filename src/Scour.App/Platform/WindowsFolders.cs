using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using Scour.Core.Platform;

namespace Scour.App.Platform;

public static class WindowsFolders
{
    private static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    public static KnownFolders Create()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) vars[k] = v;

        var user = SessionUser.Other is { } other ? ForSignedInUser(other) : null;
        user ??= new UserPaths(
            F(Environment.SpecialFolder.UserProfile),
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
            GetKnownFolder(LocalAppDataLow),
            Path.GetTempPath(),
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.Programs),
            F(Environment.SpecialFolder.Startup));

        var profile = user.Profile;
        var temp = LongPath(user.Temp).TrimEnd('\\');
        foreach (var name in new[] { "TEMP", "TMP" }) vars[name] = temp;
        vars["USERPROFILE"] = profile;
        vars["APPDATA"] = user.Roaming;
        vars["LOCALAPPDATA"] = user.Local;

        var programData = F(Environment.SpecialFolder.CommonApplicationData);
        return new KnownFolders
        {
            WindowsDir = F(Environment.SpecialFolder.Windows),
            SystemDir = F(Environment.SpecialFolder.System),
            ProgramFiles = F(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = F(Environment.SpecialFolder.ProgramFilesX86),
            CommonProgramFiles = F(Environment.SpecialFolder.CommonProgramFiles),
            CommonProgramFilesX86 = F(Environment.SpecialFolder.CommonProgramFilesX86),
            ProgramData = programData,
            UserProfile = profile,
            UsersRoot = Path.GetDirectoryName(profile) ?? @"C:\Users",
            RoamingAppData = user.Roaming,
            LocalAppData = user.Local,
            LocalLowAppData = user.LocalLow ?? Path.Combine(profile, @"AppData\LocalLow"),
            UserPrograms = Path.Combine(user.Local, "Programs"),
            UserTemp = temp,
            Documents = user.Documents,
            UserDesktop = user.Desktop,
            CommonDesktop = F(Environment.SpecialFolder.CommonDesktopDirectory),
            UserStartMenuPrograms = user.StartMenuPrograms,
            CommonStartMenuPrograms = F(Environment.SpecialFolder.CommonPrograms),
            UserStartup = user.Startup,
            CommonStartup = F(Environment.SpecialFolder.CommonStartup),
            AppDataDir = Path.Combine(programData, "Scour"),
            Variables = vars,
        };
    }

    private sealed record UserPaths(string Profile, string Roaming, string Local, string? LocalLow, string Temp,
                                    string Documents, string Desktop, string StartMenuPrograms, string Startup);

    /// <summary>
    /// Another account's folders, from its loaded registry hive (its "User Shell Folders" hold redirections such
    /// as OneDrive's Desktop and Documents). Null if the hive can't be read.
    /// </summary>
    private static UserPaths? ForSignedInUser(SignedInUser user)
    {
        try
        {
            using var shell = Microsoft.Win32.Registry.Users.OpenSubKey($@"{user.Sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            using var env = Microsoft.Win32.Registry.Users.OpenSubKey($@"{user.Sid}\Environment");
            string Expand(string raw) =>
                Environment.ExpandEnvironmentVariables(raw.Replace("%USERPROFILE%", user.ProfileDir, StringComparison.OrdinalIgnoreCase));
            string Read(Microsoft.Win32.RegistryKey? key, string name, string fallback) =>
                key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string v && v.Length > 0
                    ? Expand(v) : Path.Combine(user.ProfileDir, fallback);

            var roaming = Read(shell, "AppData", @"AppData\Roaming");
            var local = Read(shell, "Local AppData", @"AppData\Local");
            string Rel(string root, string name, string fallback)
            {
                var raw = shell?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (string.IsNullOrEmpty(raw)) return Path.Combine(root, fallback);
                return Expand(raw.Replace("%APPDATA%", roaming, StringComparison.OrdinalIgnoreCase)
                                 .Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase));
            }
            var temp = env?.GetValue("TEMP", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string t && t.Length > 0
                ? Expand(t.Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase))
                : Path.Combine(local, "Temp");
            return new UserPaths(
                user.ProfileDir, roaming, local,
                Read(shell, "{" + LocalAppDataLow.ToString().ToUpperInvariant() + "}", @"AppData\LocalLow"),
                temp,
                Read(shell, "Personal", "Documents"),
                Read(shell, "Desktop", "Desktop"),
                Rel(roaming, "Programs", @"Microsoft\Windows\Start Menu\Programs"),
                Rel(roaming, "Startup", @"Microsoft\Windows\Start Menu\Programs\Startup"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// "C:\Users\JOHNSM~1\AppData\Local\Temp" → the long form. TEMP is often stored in 8.3 form, and
    /// short paths never match the long ones found by listing folders.
    /// </summary>
    public static string LongPath(string path)
    {
        if (!path.Contains('~')) return path;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint n = NativeMethods.GetLongPathName(path, buffer, (uint)buffer.Capacity);
            return n > 0 && n < buffer.Capacity ? buffer.ToString() : path;
        }
        catch { return path; }
    }

    private static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch { return null; }
    }
}
