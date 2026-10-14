using System.IO;
using System.Text.RegularExpressions;
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
                    return code == 0 ? "Component store cleaned." : "DISM reported a problem: " + LastLine(output);
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
                    await ProcessRunner.RunHiddenAsync("takeown.exe", $"/F \"{windowsOld}\" /R /A /D Y", TimeSpan.FromMinutes(30), ct);
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
                Estimate = async ct =>
                {
                    int n = await CountShadowsAsync(drive, ct);
                    return n <= 1 ? "Nothing to remove" : $"{n - 1} restore point(s)";
                },
                Run = async (progress, ct) =>
                {
                    int n = await CountShadowsAsync(drive, ct);
                    for (int i = 0; i < n - 1; i++)
                    {
                        progress.Report($"Deleting restore point {i + 1} of {n - 1}…");
                        await ProcessRunner.RunHiddenAsync("vssadmin.exe", $"delete shadows /for={drive} /oldest /quiet", TimeSpan.FromMinutes(5), ct);
                    }
                    return n <= 1 ? "Nothing to remove." : $"Deleted {n - 1} older restore point(s).";
                },
            },
        ];
    }

    private static async Task<int> CountShadowsAsync(string drive, CancellationToken ct)
    {
        try
        {
            var (_, output) = await ProcessRunner.RunHiddenAsync("vssadmin.exe", $"list shadows /for={drive}", TimeSpan.FromMinutes(2), ct);
            return Regex.Matches(output, @"Shadow Copy ID", RegexOptions.IgnoreCase).Count;
        }
        catch
        {
            return 0;
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
