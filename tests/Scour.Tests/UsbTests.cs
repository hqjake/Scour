using System.Diagnostics;
using Scour.Core.Usb;

namespace Scour.Tests;

public class UsbTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("scour-usb").FullName;
    public void Dispose() => Directory.Delete(_tmp, true);

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["MTOOLS_SKIP_CHECK"] = "1";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static bool Has(string tool) =>
        Environment.GetEnvironmentVariable("PATH")!.Split(':').Concat(["/usr/sbin", "/sbin"]).Any(d => File.Exists(Path.Combine(d, tool)));

    [Theory]
    [InlineData(600L << 20, null)]
    [InlineData(40L << 30, null)]       // over Windows' 32 GB FAT32 limit
    [InlineData(8L << 30, 65536)]
    [InlineData(32L << 30, null)]
    public void Formatted_volume_passes_fsck_and_is_writable(long size, int? cluster)
    {
        if (!Has("fsck.fat") || !Has("mcopy")) return; // tools not installed on this machine

        var img = Path.Combine(_tmp, "vol.img");
        var layout = Fat32Formatter.Plan(size, 512, cluster);
        using (var fs = new FileStream(img, FileMode.Create))
        {
            fs.SetLength(size); // sparse
            Fat32Formatter.Format(fs, layout, hiddenSectors: 2048, "Scour usb!", 0x1234ABCD);
        }

        var fsck = Run(Has("fsck.fat") ? "fsck.fat" : "/usr/sbin/fsck.fat", $"-n -v \"{img}\"");
        Assert.True(fsck.Code == 0, fsck.Output);
        Assert.Contains("2048 hidden sectors", fsck.Output);
        Assert.Contains($"{layout.ClusterCount} data clusters", fsck.Output);
        Assert.Contains("SCOUR USB", Run("mdir", $"-i \"{img}\" ::").Output);

        var payload = Path.Combine(_tmp, "hello.txt");
        File.WriteAllText(payload, string.Concat(Enumerable.Repeat("scour ", 50_000)));
        Assert.Equal(0, Run("mcopy", $"-i \"{img}\" \"{payload}\" ::/hello.txt").Code);
        Assert.Equal(0, Run("mmd", $"-i \"{img}\" ::/EFI").Code);
        var back = Path.Combine(_tmp, "back.txt");
        Assert.Equal(0, Run("mcopy", $"-i \"{img}\" ::/hello.txt \"{back}\"").Code);
        Assert.Equal(File.ReadAllText(payload), File.ReadAllText(back));
        var again = Run(Has("fsck.fat") ? "fsck.fat" : "/usr/sbin/fsck.fat", $"-n \"{img}\"");
        Assert.True(again.Code == 0, again.Output);
    }

    [Fact]
    public void Layout_follows_microsoft_cluster_defaults_and_limits()
    {
        Assert.Equal(8, Fat32Formatter.Plan(4L << 30).SectorsPerCluster);
        Assert.Equal(64, Fat32Formatter.Plan(64L << 30).SectorsPerCluster);
        Assert.Equal(1, Fat32Formatter.Plan(1L << 30, 4096).SectorsPerCluster);
        Assert.Throws<ArgumentException>(() => Fat32Formatter.Plan(16L << 20));        // too small for FAT32
        Assert.Throws<ArgumentException>(() => Fat32Formatter.Plan(3L << 40));         // > 2 TB
        var l = Fat32Formatter.Plan(32L << 30);
        Assert.True(l.FatSectors * 512L / 4 >= l.ClusterCount + 2);                    // FAT covers every cluster
        Assert.Equal("NO NAME    ", Fat32Formatter.NormalizeLabel("*?"));
        Assert.Equal("MY STICK 20", Fat32Formatter.NormalizeLabel("my stick 2026"));
    }

    private static readonly (string, long)[] WindowsIso =
    [
        ("bootmgr", 400_000), (@"efi\boot\bootx64.efi", 2_000_000), (@"sources\boot.wim", 600_000_000),
        (@"sources\install.wim", 5_500_000_000), (@"setup.exe", 100_000),
    ];

    [Fact]
    public void Plans_windows_iso_with_wim_split()
    {
        var plan = BootPlanner.Plan(false, false, WindowsIso, PartitionScheme.Mbr, 16_000_000_000, 6_200_000_000);
        Assert.Equal(BootMode.WindowsSetup, plan.Mode);
        Assert.True(plan.SplitWim);
        Assert.Null(plan.Error);
        Assert.Contains(plan.Warnings, w => w.Contains("split"));

        var tooSmall = BootPlanner.Plan(false, false, WindowsIso, PartitionScheme.Mbr, 4_000_000_000, 6_200_000_000);
        Assert.NotNull(tooSmall.Error);
    }

    [Fact]
    public void Plans_hybrid_linux_iso_as_raw_and_rejects_raw_for_non_hybrid()
    {
        (string, long)[] linux = [(@"efi\boot\bootx64.efi", 1_000_000), (@"casper\filesystem.squashfs", 3_000_000_000)];
        Assert.Equal(BootMode.RawImage, BootPlanner.Plan(false, true, linux, PartitionScheme.Mbr, 8_000_000_000, 3_100_000_000).Mode);
        var copy = BootPlanner.Plan(false, false, linux, PartitionScheme.Gpt, 8_000_000_000, 3_100_000_000);
        Assert.Equal(BootMode.IsoCopy, copy.Mode);
        Assert.Null(copy.Error);
        var forcedRaw = BootPlanner.Plan(false, false, linux, PartitionScheme.Gpt, 8_000_000_000, 3_100_000_000, BootMode.RawImage);
        Assert.NotNull(forcedRaw.Error);
        (string, long)[] huge = [(@"efi\boot\bootx64.efi", 1_000_000), (@"live\big.squashfs", 5_000_000_000)];
        Assert.NotNull(BootPlanner.Plan(false, false, huge, PartitionScheme.Gpt, 16_000_000_000, 5_100_000_000).Error);
        Assert.Equal(BootMode.RawImage, BootPlanner.Plan(true, false, [], PartitionScheme.Mbr, 8_000_000_000, 2_000_000_000).Mode);
    }

    [Fact]
    public void Detects_hybrid_images()
    {
        var sector = new byte[512];
        Assert.False(BootPlanner.IsHybrid(sector));
        sector[510] = 0x55; sector[511] = 0xAA;
        Assert.False(BootPlanner.IsHybrid(sector)); // signature but no partitions
        sector[446 + 4] = 0x17;
        Assert.True(BootPlanner.IsHybrid(sector));
    }
}
