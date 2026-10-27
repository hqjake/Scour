using System.Runtime.InteropServices;
using Microsoft.Win32;
using Scour.Core.SystemInfo;

namespace Scour.App.Platform;

public sealed record GpuReading(string Name, double? UsagePercent, double? TemperatureC, int? FanRpm, double? PowerPercent, long? DedicatedMemoryUsed);

public sealed record DriveTemperature(string Name, int TemperatureC);

public sealed record SensorSnapshot(
    double CpuPercent,
    double? CpuMHz,
    IReadOnlyList<(string Zone, double Celsius)> ThermalZones,
    IReadOnlyList<GpuReading> Gpus,
    ulong MemoryUsed,
    ulong MemoryTotal,
    IReadOnlyList<DriveTemperature> Drives);

/// <summary>
/// Live readings that need no driver of Scour's own: CPU load and speed, ACPI thermal zones, each GPU's load,
/// memory, temperature, fan and power, and drive temperatures. CPU core temperatures need a kernel driver,
/// which Scour deliberately doesn't install (security software flags them), so they aren't shown.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private const string CpuTotal = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuPerformance = @"\Processor Information(_Total)\% Processor Performance";
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuMemory = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string ThermalZone = @"\Thermal Zone Information(*)\High Precision Temperature"; // tenths of a kelvin
    private const string ThermalZoneOld = @"\Thermal Zone Information(*)\Temperature";                // kelvin (older Windows)

    private readonly PdhQuery _pdh = new();
    private readonly double? _baseMHz;
    private List<DriveTemperature> _drives = [];
    private DateTime _drivesReadUtc;
    private List<GpuSensor>? _gpuNames;
    private readonly bool _thermalPrecise;

    public SensorReader()
    {
        foreach (var path in new[] { CpuTotal, CpuPerformance, GpuEngine, GpuMemory }) _pdh.Add(path);
        _thermalPrecise = _pdh.Add(ThermalZone);
        if (!_thermalPrecise) _pdh.Add(ThermalZoneOld);
        _pdh.Collect(); // rates need a first sample
        _baseMHz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null) is int mhz && mhz > 0 ? mhz : null;
    }

    public SensorSnapshot Sample(bool includeDrives = true)
    {
        _pdh.Collect();
        double cpu = Math.Clamp(_pdh.ReadSingle(CpuTotal) ?? 0, 0, 100);
        double? mhz = _pdh.ReadSingle(CpuPerformance) is { } perf && _baseMHz is { } b ? b * perf / 100 : null;

        var zones = _pdh.Read(_thermalPrecise ? ThermalZone : ThermalZoneOld)
            .Select(z => (Zone: z.Key, C: GpuCounters.ThermalZoneCelsius(_thermalPrecise ? z.Value / 10.0 : z.Value)))
            .Where(z => z.C is not null).Select(z => (z.Zone, z.C!.Value)).ToList();

        var usage = GpuCounters.UsageByAdapter(_pdh.Read(GpuEngine));
        var memory = GpuCounters.ByAdapter(_pdh.Read(GpuMemory));
        List<GpuSensor> sensors;
        try { sensors = GpuPerf.Read(); }
        catch { sensors = _gpuNames ?? []; }
        _gpuNames = sensors;
        var gpus = sensors.Select(s => new GpuReading(s.Name, usage.TryGetValue(s.LuidKey, out var u) ? u : null, s.TemperatureC, s.FanRpm,
                                                      s.PowerPercent, memory.TryGetValue(s.LuidKey, out var m) ? (long)m : null)).ToList();

        var mem = new ProcessNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<ProcessNative.MEMORYSTATUSEX>() };
        ProcessNative.GlobalMemoryStatusEx(ref mem);

        // Drive temperatures come from a slow WMI query; twice a minute is plenty.
        if (includeDrives && DateTime.UtcNow - _drivesReadUtc > TimeSpan.FromSeconds(30))
        {
            _drivesReadUtc = DateTime.UtcNow;
            try
            {
                _drives = SystemInfoCollector.DriveHealth().Where(d => d.TemperatureC is > 0 and < 120)
                    .Select(d => new DriveTemperature(d.Name, d.TemperatureC!.Value)).ToList();
            }
            catch { _drives = []; }
        }
        return new SensorSnapshot(cpu, mhz, zones, gpus, mem.ullTotalPhys - mem.ullAvailPhys, mem.ullTotalPhys, _drives);
    }

    public void Dispose() => _pdh.Dispose();
}
