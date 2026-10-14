namespace Scour.Core.Processes;

public sealed record ProcessNode(int Pid, int ParentPid, DateTime StartTime, string Name);

public sealed record TreeRow(int Pid, int Depth, bool HasChildren);

/// <summary>
/// Arranges processes into a parent/child tree. A parent is only trusted if it started before
/// the child — Windows reuses PIDs, so a dead parent's PID may now belong to an unrelated process.
/// </summary>
public static class ProcessTree
{
    public static List<TreeRow> Flatten(IReadOnlyCollection<ProcessNode> processes, ISet<int>? collapsed = null,
                                        IComparer<ProcessNode>? order = null)
    {
        order ??= Comparer<ProcessNode>.Create((a, b) =>
        {
            int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Pid.CompareTo(b.Pid);
        });
        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var children = new Dictionary<int, List<ProcessNode>>();
        var roots = new List<ProcessNode>();

        foreach (var p in byPid.Values)
        {
            if (TryGetParent(p, byPid, out var parent))
            {
                if (!children.TryGetValue(parent.Pid, out var list)) children[parent.Pid] = list = [];
                list.Add(p);
            }
            else
            {
                roots.Add(p);
            }
        }

        var rows = new List<TreeRow>(byPid.Count);
        var visited = new HashSet<int>();
        void Visit(ProcessNode node, int depth)
        {
            if (!visited.Add(node.Pid)) return; // defensive: cycles
            bool has = children.TryGetValue(node.Pid, out var kids) && kids.Count > 0;
            rows.Add(new TreeRow(node.Pid, depth, has));
            if (!has || collapsed?.Contains(node.Pid) == true) return;
            kids!.Sort(order);
            foreach (var k in kids) Visit(k, depth + 1);
        }
        roots.Sort(order);
        foreach (var r in roots) Visit(r, 0);

        // Anything left is part of a cycle (shouldn't happen, but never hide a process).
        foreach (var p in byPid.Values.Where(p => !visited.Contains(p.Pid) && !IsUnderCollapsed(p, byPid, collapsed)))
            Visit(p, 0);
        return rows;
    }

    private static bool TryGetParent(ProcessNode p, Dictionary<int, ProcessNode> byPid, out ProcessNode parent)
    {
        parent = null!;
        if (p.ParentPid == p.Pid || p.Pid == 0) return false;
        if (!byPid.TryGetValue(p.ParentPid, out var candidate)) return false;
        if (candidate.StartTime > p.StartTime) return false; // PID was reused
        parent = candidate;
        return true;
    }

    private static bool IsUnderCollapsed(ProcessNode p, Dictionary<int, ProcessNode> byPid, ISet<int>? collapsed)
    {
        if (collapsed is null) return false;
        var seen = new HashSet<int>();
        var cur = p;
        while (TryGetParent(cur, byPid, out var parent) && seen.Add(parent.Pid))
        {
            if (collapsed.Contains(parent.Pid)) return true;
            cur = parent;
        }
        return false;
    }

    /// <summary>All descendants of <paramref name="pid"/> (for "end process tree").</summary>
    public static List<int> Descendants(IReadOnlyCollection<ProcessNode> processes, int pid)
    {
        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var result = new List<int>();
        var queue = new Queue<int>([pid]);
        var seen = new HashSet<int> { pid };
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var p in byPid.Values)
                if (p.ParentPid == cur && TryGetParent(p, byPid, out _) && seen.Add(p.Pid))
                {
                    result.Add(p.Pid);
                    queue.Enqueue(p.Pid);
                }
        }
        return result;
    }
}

/// <summary>Turns cumulative counters (CPU time, bytes) into per-second rates between samples.</summary>
public sealed class RateTracker<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, (long Value, long Ticks)> _last = new();

    /// <returns>Units per second since the previous sample of this key (0 for the first sample).</returns>
    public double Update(TKey key, long cumulative, long timestampTicks)
    {
        double rate = 0;
        if (_last.TryGetValue(key, out var prev) && timestampTicks > prev.Ticks && cumulative >= prev.Value)
            rate = (cumulative - prev.Value) / TimeSpan.FromTicks(timestampTicks - prev.Ticks).TotalSeconds;
        _last[key] = (cumulative, timestampTicks);
        return rate;
    }

    /// <summary>Forgets keys not seen in the latest sample (exited processes).</summary>
    public void Retain(IReadOnlySet<TKey> alive)
    {
        foreach (var k in _last.Keys.Where(k => !alive.Contains(k)).ToList()) _last.Remove(k);
    }
}
