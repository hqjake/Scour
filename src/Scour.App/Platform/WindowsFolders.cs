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

        var profile = F(Environment.SpecialFolder.UserProfile);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) vars[k] = v;
        foreach (var name in new[] { "TEMP", "TMP" })
            if (vars.TryGetValue(name, out var t)) vars[name] = LongPath(t);

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
            RoamingAppData = F(Environment.SpecialFolder.ApplicationData),
            LocalAppData = local,
            LocalLowAppData = GetKnownFolder(LocalAppDataLow) ?? Path.Combine(profile, @"AppData\LocalLow"),
            UserPrograms = Path.Combine(local, "Programs"),
            UserTemp = LongPath(Path.GetTempPath()).TrimEnd('\\'),
            Documents = F(Environment.SpecialFolder.MyDocuments),
            UserDesktop = F(Environment.SpecialFolder.DesktopDirectory),
            CommonDesktop = F(Environment.SpecialFolder.CommonDesktopDirectory),
            UserStartMenuPrograms = F(Environment.SpecialFolder.Programs),
            CommonStartMenuPrograms = F(Environment.SpecialFolder.CommonPrograms),
            UserStartup = F(Environment.SpecialFolder.Startup),
            CommonStartup = F(Environment.SpecialFolder.CommonStartup),
            AppDataDir = Path.Combine(programData, "Scour"),
            Variables = vars,
        };
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
