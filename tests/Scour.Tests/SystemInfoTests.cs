using Scour.Core.SystemInfo;

namespace Scour.Tests;

public class SystemInfoTests
{
    [Theory]
    [InlineData(26, "DDR4")]
    [InlineData(34, "DDR5")]
    [InlineData(24, "DDR3")]
    [InlineData(0, "")]
    public void Decodes_memory_types(int code, string expected) => Assert.Equal(expected, HardwareCodes.MemoryType(code));

    [Fact]
    public void Decodes_other_codes()
    {
        Assert.Equal("SO-DIMM", HardwareCodes.MemoryFormFactor(12));
        Assert.Equal("NVMe", HardwareCodes.DiskBus(17));
        Assert.Equal("SSD", HardwareCodes.DiskMedia(4));
        Assert.Equal("Laptop", HardwareCodes.Chassis(10));
        Assert.Equal("x64", HardwareCodes.CpuArchitecture(9));
        Assert.Equal("Charging", HardwareCodes.BatteryStatus(6));
    }

    [Fact]
    public void Parses_wmi_dates_and_monitor_names()
    {
        Assert.Equal(new DateTime(2024, 1, 15, 9, 30, 0), HardwareCodes.WmiDate("20240115093000.000000-000"));
        Assert.Equal(new DateTime(2023, 6, 1), HardwareCodes.WmiDate("20230601"));
        Assert.Null(HardwareCodes.WmiDate("garbage"));
        Assert.Equal("DELL U2723QE", HardwareCodes.FromCodeUnits("DELL U2723QE".Select(c => (ushort)c).Concat(new ushort[4])));
        Assert.Equal(27.0, HardwareCodes.DiagonalInches(60, 34), 0);
    }

    [Fact]
    public void Formats_sizes_and_clocks()
    {
        Assert.Equal("16 GB", HardwareCodes.Bytes(17_179_869_184));
        Assert.Equal("1 TB", HardwareCodes.DecimalSize(1_000_204_886_016));
        Assert.Equal("512 GB", HardwareCodes.DecimalSize(512_110_190_592));
        Assert.Equal("3.60 GHz", HardwareCodes.Mhz(3600));
        Assert.Equal("800 MHz", HardwareCodes.Mhz(800));
    }

    [Fact]
    public void Report_includes_every_section_and_skips_empty_rows()
    {
        var cpu = new InfoSection("Processor", "x") { Summary = "Ryzen 7 7800X3D" };
        cpu.Group("CPU 1").Add("Cores", "8").Add("Empty", "  ").Add("Threads", "16");
        var mem = new InfoSection("Memory", "y") { Error = "WMI unavailable" };
        var text = SystemReport.ToText([cpu, mem], "DESKTOP-1", new DateTime(2026, 10, 1, 12, 0, 0));
        Assert.Contains("PROCESSOR", text);
        Assert.Contains("Cores    8", text);
        Assert.DoesNotContain("Empty", text);
        Assert.Contains("Not available: WMI unavailable", text);
    }
}
