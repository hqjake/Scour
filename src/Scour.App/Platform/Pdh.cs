using System.Runtime.InteropServices;

namespace Scour.App.Platform;

/// <summary>
/// Performance counters through PDH, by their English names (so they work whatever language Windows is in).
/// Wildcard counters ("\GPU Engine(*)\Utilization Percentage") return one value per instance.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    /// <summary>Adds a counter; false if this PC doesn't have it (e.g. no thermal zones).</summary>
    public bool Add(string path)
    {
        if (_query == IntPtr.Zero) return false;
        if (_counters.ContainsKey(path)) return true;
        if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) != 0) return false;
        _counters[path] = counter;
        return true;
    }

    /// <summary>Takes a sample. Rates (like utilization) need two samples before they read anything.</summary>
    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// <summary>Instance name → value for the counter (one "" entry for counters without instances).</summary>
    public Dictionary<string, double> Read(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(path, out var counter)) return result;
        uint size = 0, count = 0;
        uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0) return result;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.CStatus is not (0 or 1)) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                var name = Marshal.PtrToStringUni(item.Name) ?? "";
                result[name] = result.GetValueOrDefault(name) + item.Value;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public double? ReadSingle(string path) => Read(path) is { Count: > 0 } r ? r.Values.First() : null;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value; // PDH_FMT_COUNTERVALUE: status, then the (8-byte aligned) value union
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
