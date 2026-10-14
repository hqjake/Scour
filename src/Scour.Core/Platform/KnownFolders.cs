using System.Text.RegularExpressions;

namespace Scour.Core.Platform;

/// <summary>Well-known Windows folders plus environment variables used for expansion.</summary>
public sealed class KnownFolders
{
    public string WindowsDir { get; init; } = @"C:\Windows";
    public string SystemDir { get; init; } = @"C:\Windows\System32";
    public string ProgramFiles { get; init; } = @"C:\Program Files";
    public string ProgramFilesX86 { get; init; } = @"C:\Program Files (x86)";
    public string CommonProgramFiles { get; init; } = @"C:\Program Files\Common Files";
    public string CommonProgramFilesX86 { get; init; } = @"C:\Program Files (x86)\Common Files";
    public string ProgramData { get; init; } = @"C:\ProgramData";
    public string UserProfile { get; init; } = @"C:\Users\User";
    public string UsersRoot { get; init; } = @"C:\Users";
    public string RoamingAppData { get; init; } = @"C:\Users\User\AppData\Roaming";
    public string LocalAppData { get; init; } = @"C:\Users\User\AppData\Local";
    public string LocalLowAppData { get; init; } = @"C:\Users\User\AppData\LocalLow";
    public string UserPrograms { get; init; } = @"C:\Users\User\AppData\Local\Programs";
    public string UserTemp { get; init; } = @"C:\Users\User\AppData\Local\Temp";
    public string Documents { get; init; } = @"C:\Users\User\Documents";
    public string UserDesktop { get; init; } = @"C:\Users\User\Desktop";
    public string CommonDesktop { get; init; } = @"C:\Users\Public\Desktop";
    public string UserStartMenuPrograms { get; init; } = @"C:\Users\User\AppData\Roaming\Microsoft\Windows\Start Menu\Programs";
    public string CommonStartMenuPrograms { get; init; } = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
    public string UserStartup { get; init; } = @"C:\Users\User\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
    public string CommonStartup { get; init; } = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup";

    /// <summary>Folder holding Scour's own data (backups, traces).</summary>
    public string AppDataDir { get; init; } = @"C:\ProgramData\Scour";

    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex VarPattern = new(@"%([^%\\/:]+)%", RegexOptions.Compiled);

    /// <summary>Expands %VARIABLES% using <see cref="Variables"/> and the known folders.</summary>
    public string Expand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('%')) return text;
        return VarPattern.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (Variables.TryGetValue(name, out var v)) return v;
            return name.ToUpperInvariant() switch
            {
                "SYSTEMROOT" or "WINDIR" => WindowsDir,
                "PROGRAMFILES" or "PROGRAMW6432" => ProgramFiles,
                "PROGRAMFILES(X86)" => ProgramFilesX86,
                "COMMONPROGRAMFILES" or "COMMONPROGRAMW6432" => CommonProgramFiles,
                "COMMONPROGRAMFILES(X86)" => CommonProgramFilesX86,
                "PROGRAMDATA" or "ALLUSERSPROFILE" => ProgramData,
                "USERPROFILE" => UserProfile,
                "APPDATA" => RoamingAppData,
                "LOCALAPPDATA" => LocalAppData,
                "TEMP" or "TMP" => UserTemp,
                _ => m.Value,
            };
        });
    }

    /// <summary>Folders that must never be offered for deletion themselves.</summary>
    public IEnumerable<string> ProtectedRoots()
    {
        yield return WindowsDir;
        yield return SystemDir;
        yield return ProgramFiles;
        yield return ProgramFilesX86;
        yield return CommonProgramFiles;
        yield return CommonProgramFilesX86;
        yield return ProgramData;
        yield return UsersRoot;
        yield return UserProfile;
        yield return RoamingAppData;
        yield return LocalAppData;
        yield return LocalLowAppData;
        yield return UserPrograms;
        yield return UserTemp;
        yield return Documents;
        yield return UserDesktop;
        yield return CommonDesktop;
        yield return UserStartMenuPrograms;
        yield return CommonStartMenuPrograms;
        yield return UserStartup;
        yield return CommonStartup;
        yield return AppDataDir;
        yield return WinPath.Combine(ProgramData, "Microsoft");
        yield return WinPath.Combine(RoamingAppData, "Microsoft");
        yield return WinPath.Combine(LocalAppData, "Microsoft");
        yield return WinPath.Combine(LocalAppData, "Packages");
        yield return WinPath.Combine(ProgramData, "Package Cache");
        yield return WinPath.Combine(ProgramFiles, "WindowsApps");
        yield return WinPath.Combine(ProgramFiles, "Windows Defender");
        yield return WinPath.Combine(ProgramFiles, "Microsoft Office");
        yield return WinPath.Combine(ProgramFilesX86, "Microsoft");
        yield return WinPath.Combine(ProgramFiles, "dotnet");
        yield return WinPath.Combine(ProgramFilesX86, "dotnet");
    }
}
