using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.Core.Tracing;

public sealed class TraceResult
{
    public List<LeftoverItem> Items { get; } = [];
    public int ModifiedValueCount { get; set; }
    public List<RegKeyPath> NewUninstallKeys { get; } = [];
}

public static class SnapshotDiff
{
    /// <summary>
    /// Everything created between <paramref name="before"/> and <paramref name="after"/>, collapsed
    /// to the topmost new key/folder.
    /// </summary>
    public static TraceResult Compare(Snapshot before, Snapshot after, IEnumerable<string>? ignoredPaths = null)
    {
        var ignored = (ignoredPaths ?? []).ToList();
        var result = new TraceResult();

        var newKeys = after.Keys.Where(k => !before.Keys.ContainsKey(k.Key)).Select(k => k.Value).ToList();
        var newKeySet = new HashSet<string>(newKeys.Select(k => k.ToString()), StringComparer.OrdinalIgnoreCase);
        foreach (var key in newKeys)
        {
            if (key.Parent is { } parent && newKeySet.Contains(parent.ToString())) continue;
            result.Items.Add(LeftoverItem.ForKey(key, Confidence.Safe, "Created during installation"));
            if (key.Parent is { } p && ProgramInventory.UninstallRoots.Any(r => r.Equals(p)))
                result.NewUninstallKeys.Add(key);
        }

        foreach (var (id, v) in after.Values)
        {
            if (before.Values.TryGetValue(id, out var old))
            {
                if (old.Fingerprint != v.Fingerprint) result.ModifiedValueCount++;
                continue;
            }
            if (newKeySet.Contains(v.Key.ToString())) continue; // covered by its new key
            result.Items.Add(LeftoverItem.ForValue(v.Key, v.Name, null, Confidence.Safe, "Value created during installation"));
        }

        var newDirs = after.Directories.Keys.Where(d => !before.Directories.ContainsKey(d))
                           .Where(d => !SnapshotTaker.IsNoise(d + "\\", ignored)).ToList();
        var newDirSet = new HashSet<string>(newDirs, WinPath.Comparer);
        foreach (var dir in newDirs)
        {
            var parent = WinPath.GetDirectoryName(dir);
            if (parent is not null && newDirSet.Contains(parent)) continue;
            result.Items.Add(LeftoverItem.ForFolder(dir, Confidence.Safe, "Folder created during installation"));
        }

        foreach (var file in after.Files.Keys)
        {
            if (before.Files.ContainsKey(file) || SnapshotTaker.IsNoise(file, ignored)) continue;
            var parent = WinPath.GetDirectoryName(file);
            if (parent is not null && newDirSet.Contains(parent)) continue;
            result.Items.Add(LeftoverItem.ForFile(file, Confidence.Safe, "File created during installation"));
        }

        return result;
    }
}
