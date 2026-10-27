namespace Scour.Core.Gaming;

/// <summary>
/// Frames per second per process, from the times its frames were presented. Thread-safe: frames arrive on
/// the trace thread, the overlay reads on the UI thread.
/// </summary>
public sealed class FrameRate
{
    private static readonly long Window = TimeSpan.TicksPerSecond;
    private readonly Dictionary<int, Queue<long>> _frames = new();
    private readonly object _gate = new();

    public void Add(int pid, long timestampTicks)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<long>();
            q.Enqueue(timestampTicks);
            Trim(q, timestampTicks);
            if (q.Count > 2000) q.Dequeue(); // a runaway counter mustn't eat memory
        }
    }

    /// <summary>Frames presented in the last second, or null if the process hasn't presented any lately.</summary>
    public double? Rate(int pid, long nowTicks)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(pid, out var q)) return null;
            Trim(q, nowTicks);
            if (q.Count == 0)
            {
                _frames.Remove(pid);
                return null;
            }
            // Average interval between frames: steadier than counting, and right within a second of starting.
            if (q.Count < 2) return 1;
            var span = q.Last() - q.Peek();
            return span <= 0 ? q.Count : (q.Count - 1) * (double)TimeSpan.TicksPerSecond / span;
        }
    }

    /// <summary>The process presenting the most frames right now (the game when the overlay can't tell).</summary>
    public int? Busiest(long nowTicks)
    {
        lock (_gate)
        {
            foreach (var q in _frames.Values) Trim(q, nowTicks);
            return _frames.Where(f => f.Value.Count > 0).OrderByDescending(f => f.Value.Count).Select(f => (int?)f.Key).FirstOrDefault();
        }
    }

    private static void Trim(Queue<long> q, long now)
    {
        while (q.Count > 0 && now - q.Peek() > Window) q.Dequeue();
    }
}
