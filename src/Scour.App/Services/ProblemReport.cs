using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Scour.App.Platform;
using Scour.Core.SystemInfo;

namespace Scour.App.Services;

/// <summary>
/// Packs what's needed to diagnose a problem on someone else's PC into one zip: the error log, a system
/// report, Scour's settings and state files, and how Scour was started. No personal files are included.
/// </summary>
public static class ProblemReport
{
    public static string Create(AppServices s, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(targetFolder, $"Scour problem report {stamp}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Add(zip, "about.txt", About(s));
        bool hasLog = false;
        foreach (var log in new[] { ErrorLog.FilePath, ErrorLog.FilePath + ".old" })
            hasLog |= AddFile(zip, log, Path.GetFileName(log));
        if (!hasLog) Add(zip, "error.log", "(no errors recorded)");
        AddFile(zip, AppSettings.FilePath, "settings.json");
        AddFile(zip, Path.Combine(s.Folders.AppDataDir, "game-mode.json"), "game-mode.json");
        AddFile(zip, Path.Combine(s.Folders.AppDataDir, "history.jsonl"), "history.jsonl");

        string system;
        try { system = SystemReport.ToText(SystemInfoCollector.Collect(), Environment.MachineName, DateTime.Now); }
        catch (Exception e) { system = "Could not collect system information: " + e; }
        Add(zip, "system.txt", system);
        return zipPath;
    }

    private static string About(AppServices s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scour {UpdateService.CurrentVersion}");
        sb.AppendLine($"Report created {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Language {CultureInfo.CurrentUICulture.Name}, formats {CultureInfo.CurrentCulture.Name}, OEM code page {ProcessRunner.OemEncoding.CodePage}, ANSI code page {ProcessRunner.AnsiEncoding.CodePage}");
        sb.AppendLine($"Running as {Environment.UserDomainName}\\{Environment.UserName}, elevated={Environment.IsPrivilegedProcess}");
        sb.AppendLine($"Signed-in user: {SessionUser.OtherSignedInUser() ?? "same account"}" +
                      (SessionUser.Other is { } u ? $" (working on their profile {u.ProfileDir})" : ""));
        sb.AppendLine($"Exe: {Environment.ProcessPath}");
        sb.AppendLine($"Game mode: {(s.GameMode.IsOn ? "on" : "off")}");
        sb.AppendLine($"PowerShell: {(File.Exists(ProcessRunner.ToolPath("powershell.exe")) ? "found" : "MISSING")}, winget: {WingetService.WingetPath() ?? "not found"}");
        return sb.ToString();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>Adds a file if it exists and can be read; true if it was added.</summary>
    private static bool AddFile(ZipArchive zip, string path, string name)
    {
        try
        {
            if (!File.Exists(path)) return false;
            // Read it first: a half-created entry can't be removed from a zip being written.
            byte[] data;
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                src.CopyTo(buffer);
                data = buffer.ToArray();
            }
            using var dst = zip.CreateEntry(name).Open();
            dst.Write(data);
            return true;
        }
        catch { return false; } // unreadable: leave it out
    }
}
