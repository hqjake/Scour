using System.Buffers.Binary;
using System.Text;

namespace Scour.Core.Usb;

public sealed record Fat32Layout(int BytesPerSector, int SectorsPerCluster, int ReservedSectors, int NumFats, uint FatSectors, uint TotalSectors)
{
    public long DataStartSector => ReservedSectors + (long)NumFats * FatSectors;
    public long ClusterCount => (TotalSectors - DataStartSector) / SectorsPerCluster;
    public int ClusterBytes => BytesPerSector * SectorsPerCluster;
}

/// <summary>
/// Writes a FAT32 file system. Windows refuses to create FAT32 volumes over 32 GB, but UEFI
/// firmware boots USB sticks from FAT32 only — so bootable sticks of any size need this.
/// </summary>
public static class Fat32Formatter
{
    public const int MinClusters = 65525;

    /// <summary>Microsoft's default cluster sizes for FAT32.</summary>
    public static int DefaultClusterBytes(long volumeBytes) => volumeBytes switch
    {
        <= 8L << 30 => 4096,
        <= 16L << 30 => 8192,
        <= 32L << 30 => 16384,
        _ => 32768,
    };

    public static Fat32Layout Plan(long volumeBytes, int bytesPerSector = 512, int? clusterBytes = null)
    {
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) throw new ArgumentException("Unsupported sector size.");
        long totalSectors = volumeBytes / bytesPerSector;
        if (totalSectors > uint.MaxValue) throw new ArgumentException("FAT32 volumes are limited to 2 TB with 512-byte sectors.");
        int cluster = clusterBytes ?? DefaultClusterBytes(volumeBytes);
        if (cluster < bytesPerSector || cluster > 65536 || (cluster & (cluster - 1)) != 0) throw new ArgumentException("Invalid cluster size.");
        int spc = cluster / bytesPerSector;
        const int reserved = 32, fats = 2;

        // FAT size from the fat32format/Microsoft formula: enough 4-byte entries for every cluster.
        ulong numerator = 4UL * (ulong)(totalSectors - reserved);
        ulong denominator = (ulong)(spc * bytesPerSector) + 4UL * fats;
        uint fatSectors = (uint)(numerator / denominator + 1);
        var layout = new Fat32Layout(bytesPerSector, spc, reserved, fats, fatSectors, (uint)totalSectors);
        // The classic formula ignores the two reserved FAT entries; grow the FAT until every cluster has one.
        while ((long)layout.FatSectors * bytesPerSector / 4 < layout.ClusterCount + 2)
            layout = layout with { FatSectors = layout.FatSectors + 1 };
        if (layout.ClusterCount < MinClusters) throw new ArgumentException("The volume is too small for FAT32 with this cluster size.");
        if (layout.ClusterCount > 0x0FFFFFF5) throw new ArgumentException("Too many clusters; choose a larger cluster size.");
        return layout;
    }

    /// <param name="volume">The partition, positioned anywhere; writes are whole, aligned sectors.</param>
    /// <param name="hiddenSectors">The partition's start LBA (stored in the boot sector).</param>
    public static void Format(Stream volume, Fat32Layout l, uint hiddenSectors, string label, uint volumeId,
                              IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int bps = l.BytesPerSector;
        var zero = new byte[Math.Max(bps, 1 << 20) / bps * bps];

        // Clear reserved area, both FATs and the root directory cluster.
        long clearSectors = l.DataStartSector + l.SectorsPerCluster;
        long clearBytes = clearSectors * bps;
        volume.Position = 0;
        for (long done = 0; done < clearBytes;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(zero.Length, clearBytes - done);
            volume.Write(zero, 0, n);
            done += n;
            progress?.Report((double)done / clearBytes);
        }

        var boot = BootSector(l, hiddenSectors, label, volumeId);
        var info = FsInfo(l);
        var third = new byte[bps];
        third[510] = 0x55;
        third[511] = 0xAA;
        WriteSector(volume, 0, boot, bps);
        WriteSector(volume, 1, info, bps);
        WriteSector(volume, 2, third, bps);
        WriteSector(volume, 6, boot, bps);
        WriteSector(volume, 7, info, bps);
        WriteSector(volume, 8, third, bps);

        // First FAT sector: media descriptor, end-of-chain marker, and the root directory's cluster 2.
        var fat = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(0), 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(8), 0x0FFFFFFF);
        for (int i = 0; i < l.NumFats; i++) WriteSector(volume, l.ReservedSectors + (long)i * l.FatSectors, fat, bps);

        // Root directory: volume label entry.
        if (!string.IsNullOrWhiteSpace(label))
        {
            var root = new byte[bps];
            Encoding.ASCII.GetBytes(NormalizeLabel(label)).CopyTo(root, 0);
            root[11] = 0x08; // ATTR_VOLUME_ID
            WriteSector(volume, l.DataStartSector, root, bps);
        }
        volume.Flush();
        progress?.Report(1);
    }

    private static void WriteSector(Stream s, long sector, byte[] data, int bps)
    {
        s.Position = sector * bps;
        s.Write(data, 0, bps);
    }

    /// <summary>FAT labels: 11 chars, upper case, no characters FAT forbids.</summary>
    public static string NormalizeLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label.ToUpperInvariant())
        {
            if (sb.Length == 11) break;
            if (c < 0x20 || c > 0x7E || "\"*+,./:;<=>?[\\]|".Contains(c)) continue;
            sb.Append(c);
        }
        var s = sb.ToString();
        return (s.Length == 0 ? "NO NAME" : s).PadRight(11);
    }

    private static byte[] BootSector(Fat32Layout l, uint hidden, string label, uint volumeId)
    {
        var b = new byte[l.BytesPerSector];
        var s = b.AsSpan();
        s[0] = 0xEB; s[1] = 0x58; s[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(b, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0B..], (ushort)l.BytesPerSector);
        s[0x0D] = (byte)l.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)l.ReservedSectors);
        s[0x10] = (byte)l.NumFats;
        // 0x11 root entries = 0, 0x13 total16 = 0
        s[0x15] = 0xF8; // fixed disk
        // 0x16 FAT16 size = 0
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x18..], 63);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], 255);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], hidden);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], l.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], l.FatSectors);
        // 0x28 ext flags = 0 (FATs mirrored), 0x2A version 0.0
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x2C..], 2); // root cluster
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x30..], 1); // FSInfo sector
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x32..], 6); // backup boot sector
        s[0x40] = 0x80; // drive number
        s[0x42] = 0x29; // extended boot signature
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x43..], volumeId);
        Encoding.ASCII.GetBytes(NormalizeLabel(label)).CopyTo(b, 0x47);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(b, 0x52);

        // Tiny boot program: print a message and halt, for machines that try to boot this as a disk.
        byte[] code = [0x0E, 0x1F, 0xBE, 0x77, 0x7C, 0xAC, 0x22, 0xC0, 0x74, 0x0B, 0x56, 0xB4, 0x0E, 0xBB, 0x07, 0x00, 0xCD, 0x10, 0x5E, 0xEB, 0xF0, 0x32, 0xE4, 0xCD, 0x16, 0xCD, 0x19, 0xEB, 0xFE];
        code.CopyTo(b, 0x5A);
        Encoding.ASCII.GetBytes("This is not a bootable disk. Remove it and press any key.\r\n\0").CopyTo(b, 0x77);
        b[510] = 0x55;
        b[511] = 0xAA;
        return b;
    }

    private static byte[] FsInfo(Fat32Layout l)
    {
        var b = new byte[l.BytesPerSector];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(s[484..], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(s[488..], (uint)(l.ClusterCount - 1)); // all free but the root dir
        BinaryPrimitives.WriteUInt32LittleEndian(s[492..], 3);                            // next free hint
        BinaryPrimitives.WriteUInt32LittleEndian(s[508..], 0xAA550000);
        return b;
    }
}
