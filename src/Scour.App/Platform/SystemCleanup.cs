using System.IO;
using System.Management;
using Scour.Core.Platform;

namespace Scour.App.Platform;

/// <summary>Cleanups that need Windows tools rather than plain file deletion.</summary>
public sealed class SystemCleanupAction
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Bytes (or item count text) that would be freed; null when unknown until run.</summary>
    public required Func<CancellationToken, Task<string>> Estimate { get; init; }

    /// <summary>Runs the cleanup and returns a one-line result.</summary>
    public required Func<IProgress<string>, CancellationToken, Task<string>> Run { get; init; }
}

public static class SystemCleanup
{
    public static List<SystemCleanupAction> Create(IFileSystem fs)
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var windowsOld = Path.Combine(systemDrive, "Windows.old");
        var drive = systemDrive.TrimEnd('\\');

        return
        [
            new SystemCleanupAction
            {
                Name = "Windows component store",
                Description = "Removes superseded versions of Windows components left by updates (DISM StartComponentCleanup). Can take 10+ minutes.",
                Estimate = _ => Task.FromResult("Size is measured by DISM"),
                Run = async (progress, ct) =>
                {
                    progress.Report("Cleaning up the component store (DISM)…");
                    var (code, output) = await ProcessRunner.RunHiddenAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /NoRestart",
                                                                            TimeSpan.FromHours(1), ct);
                    // 3010: done, a restart finishes it.
                    return code is 0 or 3010 ? "Component store cleaned." : "DISM reported a problem: " + LastLine(output);
                },
            },
            new SystemCleanupAction
            {
                Name = "Previous Windows installation (Windows.old)",
                Description = "Files kept after a Windows upgrade. Deleting them removes the option to go back to the previous version.",
                Estimate = ct => Task.Run(() => Directory.Exists(windowsOld) ? Services.Format.BytesOrZero(fs.GetDirectorySize(windowsOld, ct: ct)) : "Not present", ct),
                Run = async (progress, ct) =>
                {
                    if (!Directory.Exists(windowsOld)) return "Nothing to remove.";
                    progress.Report("Taking ownership of Windows.old…");
                    // Not takeown: its "/D Y" answer is localized (J, O, S…) and rejected on non-English Windows.
                    await ProcessRunner.RunHiddenAsync("icacls.exe", $"\"{windowsOld}\" /setowner *S-1-5-32-544 /T /C /Q", TimeSpan.FromMinutes(30), ct);
                    progress.Report("Granting access to Windows.old…");
                    await ProcessRunner.RunHiddenAsync("icacls.exe", $"\"{windowsOld}\" /grant *S-1-5-32-544:F /T /C /Q", TimeSpan.FromMinutes(30), ct);
                    progress.Report("Deleting Windows.old…");
                    try
                    {
                        await Task.Run(() => fs.DeleteDirectory(windowsOld, toRecycleBin: false), ct);
                        return "Windows.old removed.";
                    }
                    catch (Exception e)
                    {
                        return "Some of Windows.old could not be removed: " + e.Message;
                    }
                },
            },
            new SystemCleanupAction
            {
                Name = "Older system restore points",
                Description = "Deletes all restore points except the most recent one.",
                Estimate = ct => Task.Run(() =>
                {
                    int n = ShadowCopies(drive).Count;
                    return n <= 1 ? "Nothing to remove" : $"{n - 1} restore point(s)";
                }, ct),
                Run = (progress, ct) => Task.Run(() =>
                {
                    var shadows = ShadowCopies(drive);
                    int deleted = 0, failed = 0;
                    foreach (var shadow in shadows.Take(shadows.Count - 1))
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report($"Deleting restore point {deleted + failed + 1} of {shadows.Count - 1}…");
                        try { shadow.Delete(); deleted++; }
                        catch { failed++; }
                    }
                    foreach (var shadow in shadows) shadow.Dispose();
                    if (shadows.Count <= 1) return "Nothing to remove.";
                    return $"Deleted {deleted} older restore point(s)." + (failed > 0 ? $" {failed} could not be deleted." : "");
                }, ct),
            },
        ];
    }

    /// <summary>
    /// The drive's shadow copies (restore points), oldest first. Read through WMI rather than vssadmin,
    /// whose text output is translated on non-English Windows.
    /// </summary>
    private static List<ManagementObject> ShadowCopies(string drive)
    {
        try
        {
            string? volume = null;
            using (var volumes = new ManagementObjectSearcher($"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{drive}'"))
                foreach (var v in volumes.Get()) using (v) volume ??= v["DeviceID"] as string;
            if (volume is null) return [];

            using var searcher = new ManagementObjectSearcher("SELECT ID, VolumeName, InstallDate FROM Win32_ShadowCopy");
            var list = new List<ManagementObject>();
            foreach (ManagementObject o in searcher.Get())
            {
                if (string.Equals(o["VolumeName"] as string, volume, StringComparison.OrdinalIgnoreCase)) list.Add(o);
                else o.Dispose();
            }
            return list.OrderBy(o => o["InstallDate"] as string ?? "", StringComparer.Ordinal).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string LastLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown error";

    /// <summary>Running processes among the given names (e.g. "chrome"), for "please close X first" warnings.</summary>
    public static List<string> Running(IEnumerable<string> processNames) =>
        processNames.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0)
                    .ToList();
}
