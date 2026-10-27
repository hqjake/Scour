using Scour.Core.Leftovers;
using Scour.Core.Platform;

namespace Scour.Core.Removal;

/// <summary>Stops and deletes Windows services. Implemented by the Windows host.</summary>
public interface IServiceController
{
    void StopAndDelete(string serviceName);
}

public sealed class RemovalOptions
{
    public bool FilesToRecycleBin { get; init; } = true;
    public bool BackupRegistry { get; init; } = true;
}

public sealed record RemovalFailure(LeftoverItem Item, string Error);

public sealed class RemovalReport
{
    public List<LeftoverItem> Removed { get; } = [];
    public List<RemovalFailure> Failed { get; } = [];
    public BackupManifest? Backup { get; set; }
    public long BytesFreed { get; set; }
}

/// <summary>Deletes selected leftovers, registry first (after backing it up), then files and folders.</summary>
public sealed class LeftoverRemover(IRegistry registry, IFileSystem fs, BackupStore? backups, IServiceController? services = null)
{
    public RemovalReport Remove(string programName, IReadOnlyList<LeftoverItem> items, RemovalOptions options,
                                DateTime nowUtc, IProgress<string>? progress = null)
    {
        var report = new RemovalReport();
        if (items.Count == 0) return report;

        if (options.BackupRegistry && backups is not null && items.Any(i => i.IsRegistry))
        {
            progress?.Report("Backing up registry");
            report.Backup = backups.Create(programName, items, registry, options.FilesToRecycleBin, nowUtc);
        }

        // Values before keys, deepest keys first, so nothing is deleted out from under another item.
        var regItems = items.Where(i => i.IsRegistry && i.RegistryKey is not null)
                            .OrderBy(i => i.Kind == LeftoverKind.RegistryValue ? 0 : 1)
                            .ThenByDescending(i => i.RegistryKey!.Value.Depth);
        foreach (var item in regItems)
        {
            progress?.Report($"Deleting {item.DisplayText}");
            try
            {
                var key = item.RegistryKey!.Value;
                if (item.Kind == LeftoverKind.RegistryValue)
                {
                    // No existence check first: a key Scour can't read looks missing, and would be reported as
                    // removed while still there. Deleting what's already gone is a no-op; denied access throws.
                    registry.DeleteValue(key, item.ValueName ?? "");
                }
                else
                {
                    if (item.ServiceName is not null && services is not null)
                    {
                        try { services.StopAndDelete(item.ServiceName); }
                        catch { /* fall back to deleting the key directly */ }
                    }
                    registry.DeleteKeyTree(key);
                }
                report.Removed.Add(item);
            }
            catch (Exception ex)
            {
                report.Failed.Add(new RemovalFailure(item, ex.Message));
            }
        }

        var fileItems = items.Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Folder)
                             .OrderBy(i => i.Kind == LeftoverKind.File ? 0 : 1)
                             .ThenByDescending(i => WinPath.Depth(i.Path));
        foreach (var item in fileItems)
        {
            progress?.Report($"Deleting {item.Path}");
            try
            {
                if (item.Kind == LeftoverKind.File)
                {
                    if (fs.FileExists(item.Path)) fs.DeleteFile(item.Path, options.FilesToRecycleBin);
                }
                else if (fs.DirectoryExists(item.Path))
                {
                    fs.DeleteDirectory(item.Path, options.FilesToRecycleBin);
                }
                report.Removed.Add(item);
                report.BytesFreed += item.SizeBytes;
            }
            catch (Exception ex)
            {
                report.Failed.Add(new RemovalFailure(item, ex.Message));
            }
        }
        return report;
    }

    /// <summary>Schedules failed file/folder deletions for the next restart. Returns how many were scheduled.</summary>
    public int ScheduleOnReboot(IEnumerable<RemovalFailure> failures)
    {
        int n = 0;
        foreach (var f in failures.Where(f => f.Item.Kind is LeftoverKind.File or LeftoverKind.Folder))
        {
            try
            {
                if (f.Item.Kind == LeftoverKind.Folder)
                {
                    // Files first, then the (now empty) folders, deepest first.
                    var dirs = new List<string>();
                    var stack = new Stack<string>();
                    stack.Push(f.Item.Path);
                    while (stack.Count > 0)
                    {
                        var d = stack.Pop();
                        dirs.Add(d);
                        foreach (var file in fs.EnumerateFiles(d)) fs.ScheduleDeleteOnReboot(file);
                        foreach (var sub in fs.EnumerateDirectories(d)) stack.Push(sub);
                    }
                    foreach (var d in dirs.OrderByDescending(WinPath.Depth)) fs.ScheduleDeleteOnReboot(d);
                }
                else
                {
                    fs.ScheduleDeleteOnReboot(f.Item.Path);
                }
                n++;
            }
            catch { /* best effort */ }
        }
        return n;
    }
}
