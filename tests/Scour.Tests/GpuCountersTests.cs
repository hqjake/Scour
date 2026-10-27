using Scour.Core.SystemInfo;

namespace Scour.Tests;

public class GpuCountersTests
{
    [Fact]
    public void Usage_is_the_busiest_engine_type_summed_over_processes()
    {
        var counters = new Dictionary<string, double>
        {
            ["pid_100_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D"] = 30,
            ["pid_200_luid_0x00000000_0x0000D1B5_phys_0_eng_0_engtype_3D"] = 25,
            ["pid_300_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_VideoDecode"] = 40,
            ["pid_100_luid_0x00000000_0x0000E000_phys_0_eng_0_engtype_3D"] = 5,
            ["pid_100_luid_0x00000001_0x0000E000_phys_0_eng_0_engtype_Copy"] = 90,
            ["garbage"] = 50,
            ["pid_9_luid_0x00000000_0x0000E000_phys_0_eng_0_engtype_3D"] = double.NaN,
        };
        var usage = GpuCounters.UsageByAdapter(counters);
        Assert.Equal(55, usage[GpuCounters.LuidKey(0, 0xD1B5)]);
        Assert.Equal(5, usage[GpuCounters.LuidKey(0, 0xE000)]);
        Assert.Equal(90, usage[GpuCounters.LuidKey(1, 0xE000)]);
        Assert.Equal(3, usage.Count);
    }

    [Fact]
    public void Memory_counters_are_summed_per_adapter()
    {
        var mem = GpuCounters.ByAdapter(new Dictionary<string, double>
        {
            ["luid_0x00000000_0x0000D1B5_phys_0"] = 1024,
            ["luid_0x00000000_0x0000D1B5_phys_1"] = 1024,
        });
        Assert.Equal(2048, mem[GpuCounters.LuidKey(0, 0xD1B5)]);
    }

    [Theory]
    [InlineData(273.15 + 45, 45.0)]
    [InlineData(0, null)]
    [InlineData(2732, null)]
    public void Thermal_zones_outside_a_believable_range_are_ignored(double kelvin, double? celsius) =>
        Assert.Equal(celsius, GpuCounters.ThermalZoneCelsius(kelvin));
}
