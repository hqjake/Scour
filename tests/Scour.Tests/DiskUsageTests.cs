using System.IO.Compression;
using Scour.Core.DiskUsage;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class DiskUsageTests
{
    private static MemoryStream LoadImage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ntfs.img.gz");
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    private static List<(bool IsDir, string Path, long Size)> Expected() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ntfs-expected.tsv"))
            .Select(l => l.Split('\t'))
            .Select(p => (p[0] == "D", p[1].Replace('/', '\\'), long.Parse(p[2])))
            .ToList();

    [Fact]
    public void Mft_scan_of_a_real_ntfs_volume_matches_the_filesystem()
    {
        using var image = LoadImage();
        var root = new MftScanner().Scan(image, @"X:\");

        var expected = Expected();
        foreach (var (isDir, path, size) in expected)
        {
            var node = root.Find(path);
            Assert.True(node is not null, $"missing {path}");
            Assert.Equal(isDir, node!.IsDirectory);
            if (!isDir) Assert.True(size == node.Size, $"{path}: expected {size}, got {node.Size}");
        }

        // Nothing invented: every user-visible entry exists on the real volume ($-metafiles aside).
        var actual = Flatten(root).Where(n => !n.Name.StartsWith('$')).Select(n => n.FullPath[4..]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Count, actual.Count);

        var photos = root.Find("Photos")!;
        Assert.Equal(400, photos.FileCount);
        Assert.Equal(expected.Where(e => e.Path.StartsWith(@"Photos\")).Sum(e => e.Size), photos.Size);
        Assert.Equal(0, root.Find("sparse.vhd")!.Allocated);          // sparse
        Assert.Equal(0, root.Find(@"Documents\Notes\todo.txt")!.Allocated); // resident in the MFT
        Assert.Equal(root.Children!.Max(c => c.Size), root.Children![0].Size); // sorted largest first
    }

    private static IEnumerable<DiskNode> Flatten(DiskNode n)
    {
        foreach (var c in n.Children ?? [])
        {
            if (c.Name.StartsWith('$')) continue;
            yield return c;
            foreach (var d in Flatten(c)) yield return d;
        }
    }

    [Fact]
    public void Decodes_data_runs_including_negative_offsets_and_sparse_runs()
    {
        // 0x21: 1-byte length 0x10, 2-byte offset 0x1000; 0x11: len 0x08, offset -0x10 (0xF0); 0x01: sparse len 4.
        byte[] runs = [0x21, 0x10, 0x00, 0x10, 0x11, 0x08, 0xF0, 0x01, 0x04, 0x00];
        Assert.Equal([new DataRun(0x1000, 0x10), new DataRun(0x1000 - 0x10, 8), new DataRun(-1, 4)], Mft.ParseRuns(runs));
    }

    [Fact]
    public void Rejects_non_ntfs_volumes()
    {
        Assert.Throws<InvalidDataException>(() => NtfsBoot.Parse(new byte[512]));
    }

    [Fact]
    public void Treemap_tiles_fill_the_bounds_proportionally_without_overlap()
    {
        double[] sizes = [600, 300, 120, 90, 60, 30, 0];
        var bounds = new TreemapRect(10, 20, 400, 300);
        var rects = Treemap.Layout(sizes, bounds);
        double total = sizes.Sum();
        for (int i = 0; i < sizes.Length - 1; i++)
        {
            Assert.Equal(bounds.Area * sizes[i] / total, rects[i].Area, 6);
            Assert.True(rects[i].X >= bounds.X - 1e-6 && rects[i].Y >= bounds.Y - 1e-6);
            Assert.True(rects[i].X + rects[i].Width <= bounds.X + bounds.Width + 1e-6);
            Assert.True(rects[i].Y + rects[i].Height <= bounds.Y + bounds.Height + 1e-6);
            for (int j = i + 1; j < sizes.Length - 1; j++)
            {
                var a = rects[i]; var b = rects[j];
                double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"tiles {i} and {j} overlap");
            }
        }
        Assert.Equal(0, rects[^1].Area);
        // Squarified: the biggest tile should not be a sliver.
        Assert.True(Math.Max(rects[0].Width / rects[0].Height, rects[0].Height / rects[0].Width) < 3);
    }

    [Fact]
    public void Folder_scan_totals_stats_and_detach()
    {
        var fs = new InMemoryFileSystem()
            .File(@"D:\a\big.iso", 5000)
            .File(@"D:\a\b\x.txt", 10)
            .File(@"D:\a\b\y.TXT", 20)
            .File(@"D:\c\movie.mkv", 3000)
            .File(@"D:\noext", 1);
        var root = new FolderScanner(fs).Scan(@"D:\");
        Assert.Equal(8031, root.Size);
        Assert.Equal(5, root.FileCount);
        Assert.Equal(3, root.FolderCount);
        Assert.Equal("a", root.Children![0].Name);

        var ext = DiskStats.ByExtension(root);
        Assert.Equal(".iso", ext[0].Extension);
        Assert.Equal(new ExtensionStat(".txt", 30, 2), ext.Single(e => e.Extension == ".txt"));
        Assert.Contains(ext, e => e.Extension == "(none)");

        Assert.Equal(["big.iso", "movie.mkv", "y.TXT"], DiskStats.LargestFiles(root, 3).Select(f => f.Name));

        var b = root.Find(@"a\b")!;
        Assert.Equal(@"D:\a\b", b.FullPath);
        b.Detach();
        Assert.Equal(8001, root.Size);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(2, root.FolderCount);
    }
}
