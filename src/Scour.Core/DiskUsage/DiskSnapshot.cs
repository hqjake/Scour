using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Scour.Core.Platform;

namespace Scour.Core.DiskUsage;

public sealed record SnapshotFolder(string Path, long Size);

public sealed record FolderChange(string Path, long OldSize, long NewSize)
{
    public long Delta => NewSize - OldSize;
    public bool IsNew => OldSize == 0;
    public bool IsGone => NewSize == 0;
}

/// <summary>
/// A compact record of a disk scan: every folder above a size threshold. Comparing two snapshots shows where space
/// went ("AppData grew 6 GB") without keeping whole scans around.
/// </summary>
public sealed class DiskSnapshot
{
    public required string Root { get; init; }
    public DateTime TakenUtc { get; init; }
    public long TotalSize { get; init; }
    public long MinFolderSize { get; init; }
    public List<SnapshotFolder> Folders { get; init; } = [];

    public static DiskSnapshot Create(DiskNode root, DateTime nowUtc, long minFolderSize = 20L << 20, int maxDepth = 12)
    {
        var folders = new List<SnapshotFolder>();
        var queue = new Queue<(DiskNode Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            folders.Add(new SnapshotFolder(node.FullPath, node.Size));
            if (depth >= maxDepth || node.Children is null) continue;
            foreach (var c in node.Children)
                if (c.IsDirectory && c.Size >= minFolderSize) queue.Enqueue((c, depth + 1));
        }
        return new DiskSnapshot { Root = root.FullPath, TakenUtc = nowUtc, TotalSize = root.Size, MinFolderSize = minFolderSize, Folders = folders };
    }

    /// <summary>
    /// Folders whose size changed by at least <paramref name="minChange"/>, reported at the level that explains the
    /// change: a folder is left out when one of its subfolders accounts for most of its growth.
    /// </summary>
    public static List<FolderChange> Compare(DiskSnapshot before, DiskSnapshot after, long minChange = 100L << 20)
    {
        var old = before.Folders.ToDictionary(f => f.Path, f => f.Size, WinPath.Comparer);
        var now = after.Folders.ToDictionary(f => f.Path, f => f.Size, WinPath.Comparer);
        var paths = old.Keys.Union(now.Keys, WinPath.Comparer).ToList();

        var changes = paths.Select(p => new FolderChange(p, old.GetValueOrDefault(p), now.GetValueOrDefault(p)))
                           .Where(c => Math.Abs(c.Delta) >= minChange)
                           .ToDictionary(c => c.Path, WinPath.Comparer);

        var children = new Dictionary<string, List<FolderChange>>(WinPath.Comparer);
        foreach (var c in changes.Values)
            if (WinPath.GetDirectoryName(c.Path) is { } parent && changes.ContainsKey(parent))
                (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(c);

        bool ExplainedByChild(FolderChange c) =>
            children.TryGetValue(c.Path, out var kids) &&
            kids.Any(k => Math.Sign(k.Delta) == Math.Sign(c.Delta) && Math.Abs(k.Delta) >= Math.Abs(c.Delta) * 0.6);

        return changes.Values.Where(c => !ExplainedByChild(c))
                      .OrderByDescending(c => c.Delta)
                      .ToList();
    }

    // ---- storage (gzipped JSON; a whole drive is typically 20–200 KB)

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Save(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        JsonSerializer.Serialize(gz, this, Json);
    }

    public static DiskSnapshot Load(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        return JsonSerializer.Deserialize<DiskSnapshot>(gz, Json) ?? throw new InvalidDataException("Empty snapshot.");
    }

    /// <summary>A file name that sorts by time: "20261015-091500.json.gz".</summary>
    public string FileName => TakenUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json.gz";

    /// <summary>Folder name for a scan root: "C:\" → "C", "D:\Games" → "D_Games".</summary>
    public static string KeyFor(string root)
    {
        var key = System.Text.RegularExpressions.Regex.Replace(root, "[^A-Za-z0-9]+", "_").Trim('_');
        return key.Length > 0 ? key : "root";
    }
}
