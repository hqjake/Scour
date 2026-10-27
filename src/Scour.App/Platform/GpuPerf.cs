using System.Runtime.InteropServices;
using Scour.Core.SystemInfo;

namespace Scour.App.Platform;

/// <summary>A graphics adapter's sensors as the driver reports them (what Task Manager shows). Nulls: not reported.</summary>
public sealed record GpuSensor(string Name, string LuidKey, double? TemperatureC, int? FanRpm, double? PowerPercent);

/// <summary>
/// GPU temperature, fan speed and power from the display driver itself (D3DKMT adapter performance data,
/// Windows 10 2004+ with a WDDM 2.4 driver). Works for NVIDIA, AMD and Intel without any vendor tools or a
/// kernel driver of Scour's own.
/// </summary>
public static class GpuPerf
{
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    public static List<GpuSensor> Read()
    {
        var result = new List<GpuSensor>();
        var enumArgs = new D3DKMT_ENUMADAPTERS2();
        if (D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0) return result;
        int infoSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        var buffer = Marshal.AllocHGlobal(infoSize * (int)enumArgs.NumAdapters);
        try
        {
            enumArgs.pAdapters = buffer;
            if (D3DKMTEnumAdapters2(ref enumArgs) != 0) return result;
            for (int i = 0; i < enumArgs.NumAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * infoSize);
                try
                {
                    var name = RegistryName(info.hAdapter);
                    if (name is null || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                    var perf = PerfData(info.hAdapter);
                    result.Add(new GpuSensor(name, GpuCounters.LuidKey(info.HighPart, info.LowPart),
                        perf is { Temperature: > 0 and < 1500 } ? perf.Value.Temperature / 10.0 : null,
                        perf is { FanRPM: > 0 and < 20000 } ? (int)perf.Value.FanRPM : null,
                        perf is { Power: > 0 and <= 1000 } ? perf.Value.Power / 10.0 : null));
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        // The same card can be listed once per display output; keep one entry.
        return result.GroupBy(g => g.LuidKey).Select(g => g.First()).ToList();
    }

    private static string? RegistryName(uint adapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTERREGISTRYINFO>();
        var data = Marshal.AllocHGlobal(size);
        try
        {
            if (Query(adapter, KMTQAITYPE_ADAPTERREGISTRYINFO, data, size) != 0) return null;
            var info = Marshal.PtrToStructure<D3DKMT_ADAPTERREGISTRYINFO>(data);
            return string.IsNullOrWhiteSpace(info.AdapterString) ? null : info.AdapterString.Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static D3DKMT_ADAPTER_PERFDATA? PerfData(uint adapter)
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 }, data, false);
            return Query(adapter, KMTQAITYPE_ADAPTERPERFDATA, data, size) == 0 ? Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(data) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static int Query(uint adapter, int type, IntPtr data, int size)
    {
        var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = data, PrivateDriverDataSize = (uint)size };
        try { return D3DKMTQueryAdapterInfo(ref q); }
        catch (EntryPointNotFoundException) { return -1; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint LowPart;   // LUID
        public int HighPart;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct D3DKMT_ADAPTERREGISTRYINFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string BiosString;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DacType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ChipType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;        // per mille of the maximum
        public uint Temperature;  // deci-degrees Celsius
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
}
