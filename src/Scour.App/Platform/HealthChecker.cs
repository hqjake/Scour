using System.IO;
using Scour.App.Services;
using Scour.Core.Autorun;
using Scour.Core.Health;
using Scour.Core.Registry;

namespace Scour.App.Platform;

public static class HealthChecker
{
    private static readonly HashSet<RegistryIssueCategory> QuickRegistryChecks =
    [
        RegistryIssueCategory.OrphanedUninstallEntries, RegistryIssueCategory.BrokenAppPaths, RegistryIssueCategory.BrokenStartupEntries,
        RegistryIssueCategory.ObsoleteShellCache, RegistryIssueCategory.ObsoleteCompatibilityEntries, RegistryIssueCategory.BrokenShortcuts,
    ];

    public static Task<HealthReport> RunAsync(AppServices s, IProgress<string> progress, CancellationToken ct = default) => Task.Run(() =>
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var drive = new DriveInfo(root);

        progress.Report("Measuring junk files");
        long junk = 0;
        foreach (var c in s.Junk.Categories.Where(c => c.EnabledByDefault))
        {
            ct.ThrowIfCancellationRequested();
            try { junk += s.Junk.Scan(c, DateTime.UtcNow, ct).TotalBytes; } catch { /* skip category */ }
        }

        progress.Report("Counting startup programs");
        int? startup = null;
        try { startup = s.Autorun.GetEntries().Count(e => e.Enabled && e.Source != AutorunSource.RegistryRunOnce); } catch { }

        progress.Report("Checking the registry");
        int? registry = null;
        try { registry = new RegistryCleaner(s.Registry, s.FileSystem, s.Folders).Scan(QuickRegistryChecks, null, ct).Count; } catch { }

        progress.Report("Checking drive health");
        var drives = SystemInfoCollector.DriveHealth();

        progress.Report("Checking security");
        var av = SystemInfoCollector.Antivirus();

        return HealthScore.Evaluate(new HealthInputs
        {
            SystemDriveFreePercent = 100.0 * drive.AvailableFreeSpace / drive.TotalSize,
            SystemDriveFreeBytes = drive.AvailableFreeSpace,
            JunkBytes = junk,
            EnabledStartupItems = startup,
            RegistryProblems = registry,
            Drives = drives,
            Antivirus = av,
            Battery = SystemInfoCollector.Battery(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            LastCleanupUtc = s.Settings.LastCleanupUtc,
            UpdatesAvailable = s.LastUpdatesCount,
            NowUtc = DateTime.UtcNow,
        });
    }, ct);
}
