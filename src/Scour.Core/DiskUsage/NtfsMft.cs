using System.Buffers.Binary;
using System.Text;

namespace Scour.Core.DiskUsage;

public sealed record NtfsBoot(int BytesPerSector, int SectorsPerCluster, long MftCluster, int RecordSize, long TotalSectors)
{
    public long ClusterSize => (long)BytesPerSector * SectorsPerCluster;

    public static NtfsBoot Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || !sector.Slice(3, 8).SequenceEqual("NTFS    "u8))
            throw new InvalidDataException("Not an NTFS volume.");
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(sector[0x0B..]);
        int spc = sector[0x0D];
        if (spc > 0x80) spc = 1 << (256 - spc); // very large clusters are stored as a negative shift
        long total = BinaryPrimitives.ReadInt64LittleEndian(sector[0x28..]);
        long mft = BinaryPrimitives.ReadInt64LittleEndian(sector[0x30..]);
        sbyte cpr = (sbyte)sector[0x40];
        int recordSize = cpr < 0 ? 1 << -cpr : cpr * bps * spc;
        if (bps is < 256 or > 4096 || recordSize is < 256 or > 65536) throw new InvalidDataException("Unsupported NTFS geometry.");
        return new NtfsBoot(bps, spc, mft, recordSize, total);
    }
}

/// <summary>One extent of a non-resident attribute. <see cref="Lcn"/> is -1 for sparse runs.</summary>
public readonly record struct DataRun(long Lcn, long Clusters);

/// <summary>The bits of an MFT file record a disk usage scan needs.</summary>
public sealed class MftRecord
{
    public long Index { get; init; }
    public long BaseIndex { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }
    public List<(long Parent, string Name, byte Namespace)> Names { get; } = [];
    public long? DataSize { get; set; }
    public long? Allocated { get; set; }
    public long LastWrite { get; set; }

    /// <summary>Runs of the unnamed $DATA attribute (only kept when requested, i.e. for $MFT itself).</summary>
    public List<(long StartVcn, List<DataRun> Runs)> DataExtents { get; } = [];

    /// <summary>Records referenced by an attribute list's $DATA entries (fragmented files / $MFT).</summary>
    public List<(long StartVcn, long Record)> DataListEntries { get; } = [];
}

public static class Mft
{
    public static List<DataRun> ParseRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<DataRun>();
        long lcn = 0;
        int pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            int header = data[pos++];
            int lenBytes = header & 0x0F, offBytes = header >> 4;
            if (lenBytes == 0 || pos + lenBytes + offBytes > data.Length) break;
            long length = ReadSigned(data.Slice(pos, lenBytes), signed: false);
            pos += lenBytes;
            if (offBytes == 0)
            {
                runs.Add(new DataRun(-1, length));
                continue;
            }
            lcn += ReadSigned(data.Slice(pos, offBytes), signed: true);
            pos += offBytes;
            runs.Add(new DataRun(lcn, length));
        }
        return runs;
    }

    private static long ReadSigned(ReadOnlySpan<byte> b, bool signed)
    {
        long v = 0;
        for (int i = b.Length - 1; i >= 0; i--) v = (v << 8) | b[i];
        if (signed && b.Length < 8 && (b[^1] & 0x80) != 0) v -= 1L << (8 * b.Length);
        return v;
    }

    /// <summary>Undoes the update-sequence protection on a record. Returns false if the record is torn.</summary>
    public static bool ApplyFixup(Span<byte> record, int bytesPerSector = 512)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length) return false;
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            // The update-sequence stride is 512 bytes on every NTFS volume, whatever the sector size.
            int end = i * 512 - 2;
            if (end + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn) return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    public static MftRecord? ParseRecord(ReadOnlySpan<byte> rec, long index, bool keepRuns = false)
    {
        if (rec.Length < 48 || !rec[..4].SequenceEqual("FILE"u8)) return null;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        long baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & 0x0000FFFFFFFFFFFF);
        int attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        var r = new MftRecord { Index = index, BaseIndex = baseRef, InUse = (flags & 1) != 0, IsDirectory = (flags & 2) != 0 };
        if (!r.InUse) return r;

        int pos = attrOffset;
        while (pos + 16 <= rec.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[pos..]);
            if (type == 0xFFFFFFFF) break;
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[(pos + 4)..]);
            if (len < 16 || pos + len > rec.Length) break;
            var attr = rec.Slice(pos, len);
            bool nonResident = attr[8] != 0;
            int nameLength = attr[9];

            if (type == 0x10 && !nonResident) // $STANDARD_INFORMATION
            {
                int off = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                if (off + 16 <= attr.Length) r.LastWrite = BinaryPrimitives.ReadInt64LittleEndian(attr[(off + 8)..]);
            }
            else if (type == 0x30 && !nonResident) // $FILE_NAME
            {
                int off = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                var c = attr[off..];
                long parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(c) & 0x0000FFFFFFFFFFFF);
                int chars = c[0x40];
                byte ns = c[0x41];
                if (0x42 + chars * 2 <= c.Length)
                    r.Names.Add((parent, Encoding.Unicode.GetString(c.Slice(0x42, chars * 2)), ns));
            }
            else if (type == 0x80 && nameLength == 0) // unnamed $DATA
            {
                if (!nonResident)
                {
                    r.DataSize = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                    r.Allocated = 0;
                }
                else
                {
                    long startVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]);
                    if (startVcn == 0)
                    {
                        ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x0C..]);
                        long allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x28..]);
                        if ((attrFlags & 0x8001) != 0 && attr.Length >= 0x48) allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x40..]);
                        r.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                        r.Allocated = allocated;
                    }
                    if (keepRuns)
                    {
                        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                        r.DataExtents.Add((startVcn, ParseRuns(attr[runOffset..])));
                    }
                }
            }
            else if (type == 0x20 && !nonResident && keepRuns) // $ATTRIBUTE_LIST (resident)
            {
                int off = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
                int valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
                var list = attr.Slice(off, Math.Min(valueLength, attr.Length - off));
                int p = 0;
                while (p + 0x1A <= list.Length)
                {
                    uint t = BinaryPrimitives.ReadUInt32LittleEndian(list[p..]);
                    int entryLen = BinaryPrimitives.ReadUInt16LittleEndian(list[(p + 4)..]);
                    if (entryLen == 0) break;
                    if (t == 0x80 && list[p + 6] == 0)
                    {
                        long vcn = BinaryPrimitives.ReadInt64LittleEndian(list[(p + 8)..]);
                        long recNo = (long)(BinaryPrimitives.ReadUInt64LittleEndian(list[(p + 0x10)..]) & 0x0000FFFFFFFFFFFF);
                        if (recNo != index) r.DataListEntries.Add((vcn, recNo));
                    }
                    p += entryLen;
                }
            }
            pos += len;
        }
        return r;
    }
}

/// <summary>
/// Reads a whole NTFS volume's Master File Table and builds the folder tree from it — the
/// technique WizTree uses. Reading the MFT sequentially is far faster than walking folders.
/// </summary>
public sealed class MftScanner
{
    private const int ChunkBytes = 4 * 1024 * 1024;
    private const long RootIndex = 5;

    public DiskNode Scan(Stream volume, string rootName, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var boot = NtfsBoot.Parse(ReadAt(volume, 0, 4096));
        var mft = ReadRecord(volume, boot, boot.MftCluster * boot.ClusterSize, 0, keepRuns: true)
                  ?? throw new InvalidDataException("Cannot read the MFT.");
        var extents = new List<(long StartVcn, List<DataRun> Runs)>(mft.DataExtents);

        // A fragmented $MFT keeps the rest of its run list in extension records.
        foreach (var (_, recNo) in mft.DataListEntries.Distinct())
        {
            var offset = OffsetOfRecord(extents, boot, recNo);
            if (offset is null) continue;
            var ext = ReadRecord(volume, boot, offset.Value, recNo, keepRuns: true);
            if (ext is not null) extents.AddRange(ext.DataExtents);
        }
        var runs = extents.OrderBy(e => e.StartVcn).SelectMany(e => e.Runs).ToList();
        long totalRecords = (mft.DataSize ?? 0) / boot.RecordSize;

        var builder = new Builder(totalRecords);
        long index = 0;
        var buffer = new byte[ChunkBytes];
        foreach (var run in runs)
        {
            long runBytes = run.Clusters * boot.ClusterSize;
            for (long done = 0; done < runBytes && index < totalRecords; done += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                int len = (int)Math.Min(ChunkBytes, runBytes - done);
                if (run.Lcn < 0) { index += len / boot.RecordSize; continue; }
                volume.Position = run.Lcn * boot.ClusterSize + done;
                volume.ReadExactly(buffer, 0, len);
                for (int off = 0; off + boot.RecordSize <= len && index < totalRecords; off += boot.RecordSize, index++)
                {
                    var span = buffer.AsSpan(off, boot.RecordSize);
                    if (!span[..4].SequenceEqual("FILE"u8) || !Mft.ApplyFixup(span, boot.BytesPerSector)) continue;
                    var rec = Mft.ParseRecord(span, index);
                    if (rec is { InUse: true }) builder.Add(rec);
                }
                progress?.Report(totalRecords == 0 ? 1 : (double)index / totalRecords);
            }
        }
        return builder.Build(rootName);
    }

    private static long? OffsetOfRecord(List<(long StartVcn, List<DataRun> Runs)> extents, NtfsBoot boot, long record)
    {
        long byteOffset = record * boot.RecordSize;
        long vcn = byteOffset / boot.ClusterSize;
        foreach (var (start, runs) in extents.OrderBy(e => e.StartVcn))
        {
            long cur = start;
            foreach (var r in runs)
            {
                if (vcn < cur + r.Clusters)
                    return r.Lcn < 0 ? null : (r.Lcn + (vcn - cur)) * boot.ClusterSize + byteOffset % boot.ClusterSize;
                cur += r.Clusters;
            }
        }
        return null;
    }

    private static MftRecord? ReadRecord(Stream volume, NtfsBoot boot, long offset, long index, bool keepRuns)
    {
        // Raw volume reads must be sector aligned.
        long aligned = offset / 4096 * 4096;
        int skip = (int)(offset - aligned);
        var buf = ReadAt(volume, aligned, (skip + boot.RecordSize + 4095) / 4096 * 4096);
        var span = buf.AsSpan(skip, boot.RecordSize);
        return Mft.ApplyFixup(span, boot.BytesPerSector) ? Mft.ParseRecord(span, index, keepRuns) : null;
    }

    private static byte[] ReadAt(Stream s, long offset, int count)
    {
        var buf = new byte[count];
        s.Position = offset;
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return buf;
    }

    private sealed class Builder(long capacity)
    {
        private readonly Dictionary<long, Entry> _entries = new((int)Math.Min(capacity, 8_000_000));

        private sealed class Entry
        {
            public long Parent = -1;
            public string? Name;
            public byte Namespace = 255;
            public bool IsDirectory;
            public long Size;
            public long Allocated;
            public long LastWrite;
            public bool HasBase;
        }

        private Entry Get(long index)
        {
            if (!_entries.TryGetValue(index, out var e)) _entries[index] = e = new Entry();
            return e;
        }

        public void Add(MftRecord r)
        {
            var target = r.BaseIndex != 0 ? r.BaseIndex : r.Index;
            var e = Get(target);
            if (r.BaseIndex == 0)
            {
                e.HasBase = true;
                e.IsDirectory = r.IsDirectory;
                if (r.LastWrite != 0) e.LastWrite = r.LastWrite;
            }
            foreach (var (parent, name, ns) in r.Names)
            {
                // Prefer the long Win32 name over the 8.3 DOS alias (namespace 2).
                int rank = ns == 2 ? 3 : ns == 0 ? 2 : 1;
                int current = e.Namespace == 255 ? 9 : e.Namespace == 2 ? 3 : e.Namespace == 0 ? 2 : 1;
                if (rank < current)
                {
                    e.Parent = parent;
                    e.Name = name;
                    e.Namespace = ns;
                }
            }
            if (r.DataSize is { } size) e.Size = size;
            if (r.Allocated is { } alloc) e.Allocated = alloc;
        }

        public DiskNode Build(string rootName)
        {
            var nodes = new Dictionary<long, DiskNode>(_entries.Count);
            var root = new DiskNode(rootName, true);
            nodes[RootIndex] = root;
            foreach (var (index, e) in _entries)
            {
                if (index == RootIndex || !e.HasBase || e.Name is null) continue;
                var node = new DiskNode(e.Name, e.IsDirectory)
                {
                    Size = e.IsDirectory ? 0 : e.Size,
                    Allocated = e.IsDirectory ? 0 : e.Allocated,
                    ModifiedUtc = e.LastWrite > 0 ? DateTime.FromFileTimeUtc(e.LastWrite) : default,
                };
                nodes[index] = node;
            }
            foreach (var (index, node) in nodes)
            {
                if (index == RootIndex) continue;
                var e = _entries[index];
                if (nodes.TryGetValue(e.Parent, out var parent) && parent.IsDirectory && !ReferenceEquals(parent, node))
                    parent.AddChild(node);
            }
            // Keep only what hangs off the root (drops orphans from half-deleted records).
            root.ComputeTotals();
            return root;
        }
    }
}
