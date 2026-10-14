using System.Security.Cryptography;
using Scour.Core.Platform;

namespace Scour.Core.Junk;

public sealed record FoundFile(string Path, long Size, DateTime ModifiedUtc);

public sealed class DuplicateGroup
{
    public required long Size { get; init; }
    public required string Hash { get; init; }
    public required List<FoundFile> Files { get; init; }

    /// <summary>Space freed by keeping just one copy.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

public sealed class FinderOptions
{
    public long MinSize { get; init; } = 1024 * 1024;
    public bool SkipHiddenSystemRoots { get; init; } = true;

    /// <summary>Folder names never descended into (Windows, package caches, VCS metadata).</summary>
    public HashSet<string> ExcludedFolderNames { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "WinSxS", "WindowsApps", ".git", "node_modules",
    };
}

/// <summary>Finds duplicate files (by size, then partial hash, then full SHA-256) and large/old files.</summary>
public sealed class FileFinders(IFileSystem fs)
{
    private const int PartialBytes = 64 * 1024;

    public IEnumerable<FoundFile> Enumerate(IEnumerable<string> roots, FinderOptions options, CancellationToken ct)
    {
        var stack = new Stack<string>(roots.Where(fs.DirectoryExists));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var f in fs.EnumerateFiles(dir))
            {
                long size;
                DateTime modified;
                try
                {
                    size = fs.GetFileSize(f);
                    modified = fs.GetLastWriteTimeUtc(f);
                }
                catch { continue; }
                if (size >= options.MinSize) yield return new FoundFile(f, size, modified);
            }
            foreach (var d in fs.EnumerateDirectories(dir))
                if (!options.ExcludedFolderNames.Contains(WinPath.GetFileName(d))) stack.Push(d);
        }
    }

    public List<DuplicateGroup> FindDuplicates(IEnumerable<string> roots, FinderOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Listing files");
        var bySize = Enumerate(roots, options, ct)
            .DistinctBy(f => f.Path, WinPath.Comparer)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        var result = new List<DuplicateGroup>();
        int done = 0;
        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Comparing files ({++done:N0} of {bySize.Count:N0} size groups)");
            var byPartial = sizeGroup.GroupBy(f => TryHash(f.Path, PartialBytes)).Where(g => g.Key is not null && g.Count() > 1);
            foreach (var partialGroup in byPartial)
            {
                IEnumerable<IGrouping<string?, FoundFile>> final = sizeGroup.Key <= PartialBytes
                    ? [partialGroup]
                    : partialGroup.GroupBy(f => TryHash(f.Path, long.MaxValue)).Where(g => g.Key is not null && g.Count() > 1);
                foreach (var g in final)
                    result.Add(new DuplicateGroup
                    {
                        Size = sizeGroup.Key,
                        Hash = g.Key!,
                        Files = g.OrderBy(f => f.ModifiedUtc).ToList(),
                    });
            }
        }
        return result.OrderByDescending(g => g.Wasted).ToList();
    }

    public List<FoundFile> FindLargeFiles(IEnumerable<string> roots, FinderOptions options, DateTime? notModifiedSinceUtc = null,
                                          int max = 1000, CancellationToken ct = default) =>
        Enumerate(roots, options, ct)
            .Where(f => notModifiedSinceUtc is null || f.ModifiedUtc < notModifiedSinceUtc)
            .OrderByDescending(f => f.Size)
            .Take(max)
            .ToList();

    private string? TryHash(string path, long maxBytes)
    {
        try
        {
            using var stream = fs.OpenRead(path);
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long remaining = maxBytes;
            int read;
            while (remaining > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                remaining -= read;
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch
        {
            return null;
        }
    }
}
