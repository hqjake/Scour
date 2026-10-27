namespace Scour.Core.Gaming;

/// <summary>
/// Frames per second per process, from the times its frames were presented. Thread-safe: frames arrive on
/// the trace thread, the overlay reads on the UI thread.
/// </summary>
/// <remarks>
/// Real-time traces arrive in batches about once a second, so the rate is measured over the last second
/// before the newest frame received, not before "now"; a process only counts as stopped once nothing has
/// arrived from it for a few seconds.
/// </remarks>
public sealed class FrameRate
{
    private static readonly long Window = TimeSpan.TicksPerSecond;
    private static readonly long Stale = 3 * TimeSpan.TicksPerSecond;
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

    /// <summary>Frames per second over the latest second of frames, or null if the process hasn't presented any lately.</summary>
    public double? Rate(int pid, long nowTicks)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(pid, out var q) || q.Count == 0) return null;
            if (nowTicks - q.Last() > Stale)
            {
                _frames.Remove(pid);
                return null;
            }
            if (q.Count < 2) return 1;
            // Average interval between frames: steadier than counting, and right within a second of starting.
            var span = q.Last() - q.Peek();
            return span <= 0 ? q.Count : (q.Count - 1) * (double)TimeSpan.TicksPerSecond / span;
        }
    }

    /// <summary>The process presenting the most frames right now (the game when the overlay can't tell).</summary>
    public int? Busiest(long nowTicks)
    {
        lock (_gate)
        {
            foreach (var pid in _frames.Where(f => f.Value.Count == 0 || nowTicks - f.Value.Last() > Stale).Select(f => f.Key).ToList())
                _frames.Remove(pid);
            return _frames.OrderByDescending(f => f.Value.Count).Select(f => (int?)f.Key).FirstOrDefault();
        }
    }

    /// <summary>Keeps one second of frames, counted back from the newest.</summary>
    private static void Trim(Queue<long> q, long newest)
    {
        while (q.Count > 0 && newest - q.Peek() > Window) q.Dequeue();
    }
}
