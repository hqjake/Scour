using Scour.Core.Platform;

namespace Scour.Core.DiskUsage;

/// <summary>Ordinary folder-by-folder scan, used for non-NTFS drives, single folders, or when raw access is refused.</summary>
public sealed class FolderScanner(IFileSystem fs)
{
    public DiskNode Scan(string rootPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var root = new DiskNode(WinPath.Normalize(rootPath), true);
        var stack = new Stack<(DiskNode Node, string Path)>();
        stack.Push((root, rootPath));
        int dirs = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (node, path) = stack.Pop();
            if (++dirs % 500 == 0) progress?.Report(path);
            foreach (var f in fs.EnumerateFiles(path))
            {
                long size = 0;
                DateTime modified = default;
                try
                {
                    size = fs.GetFileSize(f);
                    modified = fs.GetLastWriteTimeUtc(f);
                }
                catch { /* vanished */ }
                node.AddChild(new DiskNode(WinPath.GetFileName(f), false) { Size = size, Allocated = size, ModifiedUtc = modified });
            }
            foreach (var d in fs.EnumerateDirectories(path))
            {
                var child = new DiskNode(WinPath.GetFileName(d), true);
                node.AddChild(child);
                stack.Push((child, d));
            }
        }
        root.ComputeTotals();
        return root;
    }
}
