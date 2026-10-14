using System.Text;

namespace Scour.Core.DiskUsage;

/// <summary>A file or folder in a disk usage scan. Folder sizes are the totals of their contents.</summary>
public sealed class DiskNode(string name, bool isDirectory, DiskNode? parent = null)
{
    public string Name { get; internal set; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public DiskNode? Parent { get; internal set; } = parent;
    public List<DiskNode>? Children { get; internal set; } = isDirectory ? [] : null;

    /// <summary>Logical size in bytes.</summary>
    public long Size { get; internal set; }

    /// <summary>Space actually used on disk.</summary>
    public long Allocated { get; internal set; }

    public int FileCount { get; internal set; }
    public int FolderCount { get; internal set; }
    public DateTime ModifiedUtc { get; internal set; }

    public string FullPath
    {
        get
        {
            var parts = new Stack<string>();
            for (var n = this; n is not null; n = n.Parent) parts.Push(n.Name);
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
                sb.Append(p);
            }
            return sb.ToString();
        }
    }

    public string Extension
    {
        get
        {
            if (IsDirectory) return "";
            int dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? "(none)" : Name[dot..].ToLowerInvariant();
        }
    }

    public void AddChild(DiskNode child)
    {
        child.Parent = this;
        (Children ??= []).Add(child);
    }

    /// <summary>Recomputes folder totals bottom-up and sorts children largest first.</summary>
    public void ComputeTotals()
    {
        // Iterative post-order: real drives are deep enough to overflow recursion.
        var stack = new Stack<(DiskNode Node, bool Visited)>();
        stack.Push((this, false));
        while (stack.Count > 0)
        {
            var (node, visited) = stack.Pop();
            if (!node.IsDirectory) continue;
            if (!visited)
            {
                stack.Push((node, true));
                foreach (var c in node.Children!) if (c.IsDirectory) stack.Push((c, false));
                continue;
            }
            long size = 0, alloc = 0;
            int files = 0, folders = 0;
            foreach (var c in node.Children!)
            {
                size += c.Size;
                alloc += c.Allocated;
                if (c.IsDirectory)
                {
                    files += c.FileCount;
                    folders += c.FolderCount + 1;
                }
                else
                {
                    files++;
                }
            }
            node.Size = size;
            node.Allocated = alloc;
            node.FileCount = files;
            node.FolderCount = folders;
            node.Children!.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }

    /// <summary>Removes this node from its parent and subtracts its totals up the chain.</summary>
    public void Detach()
    {
        if (Parent is null) return;
        Parent.Children!.Remove(this);
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.Allocated -= Allocated;
            p.FileCount -= IsDirectory ? FileCount : 1;
            if (IsDirectory) p.FolderCount -= FolderCount + 1;
        }
        Parent = null;
    }

    public IEnumerable<DiskNode> DescendantFiles()
    {
        var stack = new Stack<DiskNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDirectory) { yield return n; continue; }
            foreach (var c in n.Children!) stack.Push(c);
        }
    }

    public DiskNode? Find(string relativePath)
    {
        var node = this;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children?.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    public override string ToString() => $"{Name} ({Size:N0} bytes)";
}

public sealed record ExtensionStat(string Extension, long Size, int Count);

public static class DiskStats
{
    public static List<ExtensionStat> ByExtension(DiskNode root) =>
        root.DescendantFiles()
            .GroupBy(f => f.Extension)
            .Select(g => new ExtensionStat(g.Key, g.Sum(f => f.Size), g.Count()))
            .OrderByDescending(s => s.Size)
            .ToList();

    public static List<DiskNode> LargestFiles(DiskNode root, int count)
    {
        var heap = new PriorityQueue<DiskNode, long>();
        foreach (var f in root.DescendantFiles())
        {
            if (heap.Count < count) heap.Enqueue(f, f.Size);
            else if (f.Size > heap.Peek().Size) heap.EnqueueDequeue(f, f.Size);
        }
        var list = new List<DiskNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }
}
