namespace Scour.Core.Usb;

public enum PartitionScheme
{
    /// <summary>MBR: boots on legacy BIOS and on UEFI.</summary>
    Mbr,
    /// <summary>GPT: UEFI only.</summary>
    Gpt,
}

public enum BootMode
{
    /// <summary>FAT32, copy the Windows setup files (splitting install.wim if needed), bootsect for BIOS.</summary>
    WindowsSetup,
    /// <summary>FAT32, copy the ISO's files (UEFI boots from \EFI\BOOT).</summary>
    IsoCopy,
    /// <summary>Write the image byte-for-byte to the disk (hybrid Linux ISOs, .img files).</summary>
    RawImage,
}

public sealed class BootPlan
{
    public required BootMode Mode { get; init; }
    public bool SplitWim { get; init; }
    public string? WimRelativePath { get; init; }
    public long BytesToCopy { get; init; }
    public List<string> Warnings { get; } = [];
    public string? Error { get; set; }
}

public static class BootPlanner
{
    public const long Fat32MaxFile = 0xFFFFFFFFL; // 4 GB − 1

    /// <summary>
    /// True if the image starts with an MBR that has at least one partition (an "isohybrid" image that
    /// can be written raw to a USB stick).
    /// </summary>
    public static bool IsHybrid(ReadOnlySpan<byte> firstSector)
    {
        if (firstSector.Length < 512 || firstSector[510] != 0x55 || firstSector[511] != 0xAA) return false;
        for (int i = 0; i < 4; i++)
            if (firstSector[446 + i * 16 + 4] != 0) return true; // partition type byte
        return false;
    }

    public static bool IsWindowsSetup(IReadOnlyCollection<string> relativePaths) =>
        relativePaths.Any(p => p.Equals(@"sources\install.wim", StringComparison.OrdinalIgnoreCase) ||
                               p.Equals(@"sources\install.esd", StringComparison.OrdinalIgnoreCase) ||
                               p.Equals(@"sources\install.swm", StringComparison.OrdinalIgnoreCase)) &&
        relativePaths.Any(p => p.Equals("bootmgr", StringComparison.OrdinalIgnoreCase) || p.Equals(@"efi\boot\bootx64.efi", StringComparison.OrdinalIgnoreCase));

    /// <param name="isImageFile">A raw .img/.bin/.raw image rather than an ISO.</param>
    /// <param name="files">Files inside the mounted ISO (empty for raw images).</param>
    /// <param name="requested">Mode the user picked, or null for automatic.</param>
    public static BootPlan Plan(bool isImageFile, bool hybrid, IReadOnlyList<(string Path, long Size)> files, PartitionScheme scheme,
                                long targetBytes, long imageBytes, BootMode? requested = null)
    {
        var paths = files.Select(f => f.Path).ToList();
        bool windows = !isImageFile && IsWindowsSetup(paths);
        var mode = requested ?? (isImageFile ? BootMode.RawImage : windows ? BootMode.WindowsSetup : hybrid ? BootMode.RawImage : BootMode.IsoCopy);

        if (mode == BootMode.RawImage)
        {
            var raw = new BootPlan { Mode = mode, BytesToCopy = imageBytes };
            if (!isImageFile && !hybrid)
                raw.Error = "This ISO isn't a hybrid image, so writing it raw won't boot. Use file copy mode instead.";
            if (windows)
                raw.Warnings.Add("Windows ISOs don't boot when written raw; Windows setup mode is recommended.");
            if (imageBytes > targetBytes) raw.Error = "The image is larger than the USB drive.";
            raw.Warnings.Add("The drive will show only the image's own partitions afterwards. Format it again to reuse it.");
            return raw;
        }

        if (isImageFile)
            return new BootPlan { Mode = BootMode.RawImage, Error = "Disk images (.img) can only be written raw." };

        var wim = files.FirstOrDefault(f => f.Path.Equals(@"sources\install.wim", StringComparison.OrdinalIgnoreCase));
        bool split = mode == BootMode.WindowsSetup && wim.Path is not null && wim.Size > Fat32MaxFile;
        var plan = new BootPlan
        {
            Mode = mode,
            SplitWim = split,
            WimRelativePath = split ? wim.Path : null,
            BytesToCopy = files.Sum(f => f.Size),
        };

        var tooBig = files.Where(f => f.Size > Fat32MaxFile && !(split && f.Path == wim.Path)).Select(f => f.Path).ToList();
        if (tooBig.Count > 0)
            plan.Error = $"FAT32 can't hold files over 4 GB ({string.Join(", ", tooBig)})." +
                         (hybrid ? " Try raw image mode." : "");
        if (plan.BytesToCopy > targetBytes - (64L << 20)) plan.Error = "The USB drive is too small for this ISO.";

        bool hasEfi = paths.Any(p => p.StartsWith(@"efi\boot\boot", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".efi", StringComparison.OrdinalIgnoreCase));
        if (!hasEfi) plan.Warnings.Add("No UEFI boot loader found (\\EFI\\BOOT\\BOOTx64.EFI); the stick may not boot on modern PCs.");
        if (mode == BootMode.IsoCopy && scheme == PartitionScheme.Mbr)
            plan.Warnings.Add("Legacy BIOS boot needs the ISO's own boot loader to support FAT32; UEFI boot will work if the ISO has an EFI folder.");
        if (mode == BootMode.WindowsSetup && scheme == PartitionScheme.Gpt)
            plan.Warnings.Add("GPT sticks boot on UEFI PCs only. Choose MBR to also support old BIOS PCs.");
        if (split) plan.Warnings.Add("install.wim is larger than 4 GB and will be split into install.swm parts (Windows setup reads them automatically).");
        return plan;
    }
}
