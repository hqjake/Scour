using Scour.Core.DiskUsage;

namespace Scour.Tests;

public class DiskSnapshotTests
{
    private const long MB = 1L << 20;

    /// <summary>Builds a tree from "path=sizeMB" files under C:\.</summary>
    private static DiskNode Tree(params (string Path, long Mb)[] files)
    {
        var root = new DiskNode(@"C:\", true);
        foreach (var (path, mb) in files)
        {
            var node = root;
            var parts = path.Split('\\');
            foreach (var dir in parts[..^1])
            {
                var next = node.Children!.FirstOrDefault(c => c.IsDirectory && c.Name == dir);
                if (next is null) node.AddChild(next = new DiskNode(dir, true));
                node = next;
            }
            node.AddChild(new DiskNode(parts[^1], false) { Size = mb * MB });
        }
        root.ComputeTotals();
        return root;
    }

    [Fact]
    public void Reports_growth_at_the_folder_that_explains_it()
    {
        var before = DiskSnapshot.Create(Tree(
            (@"Users\Alex\AppData\Local\Temp\a.tmp", 100),
            (@"Users\Alex\Videos\trip.mp4", 2000),
            (@"Games\Old\data.pak", 5000),
            (@"Windows\System32\big.dll", 900)), DateTime.UtcNow.AddDays(-7));
        var after = DiskSnapshot.Create(Tree(
            (@"Users\Alex\AppData\Local\Temp\a.tmp", 100),
            (@"Users\Alex\AppData\Local\Temp\b.tmp", 6000),
            (@"Users\Alex\Videos\trip.mp4", 2000),
            (@"Users\Alex\Videos\new.mp4", 300),
            (@"Games\New\data.pak", 4000),
            (@"Windows\System32\big.dll", 905)), DateTime.UtcNow);

        var changes = DiskSnapshot.Compare(before, after);
        var paths = changes.Select(c => c.Path).ToList();

        // Temp explains Local, AppData, Alex, Users and C:\ growing, so only Temp is listed.
        Assert.Equal(@"C:\Users\Alex\AppData\Local\Temp", paths[0]);
        Assert.Equal(6000 * MB, changes[0].Delta);
        Assert.Contains(@"C:\Users\Alex\Videos", paths);
        Assert.DoesNotContain(@"C:\Users\Alex\AppData", paths);
        Assert.DoesNotContain(@"C:\Users", paths);
        // A folder that appeared and one that vanished.
        Assert.True(changes.Single(c => c.Path == @"C:\Games\New").IsNew);
        Assert.True(changes.Single(c => c.Path == @"C:\Games\Old").IsGone);
        Assert.Equal(@"C:\Games\Old", changes.Last().Path); // biggest shrink last
        Assert.DoesNotContain(@"C:\Windows\System32", paths); // +5 MB is noise
    }

    [Fact]
    public void Round_trips_and_names_files()
    {
        var snap = DiskSnapshot.Create(Tree((@"a\b\c.bin", 50), (@"a\small.bin", 1)), new DateTime(2026, 10, 15, 9, 15, 0, DateTimeKind.Utc));
        Assert.Equal([@"C:\", @"C:\a", @"C:\a\b"], snap.Folders.Select(f => f.Path));
        using var ms = new MemoryStream();
        snap.Save(ms);
        ms.Position = 0;
        var back = DiskSnapshot.Load(ms);
        Assert.Equal(snap.Folders, back.Folders);
        Assert.Equal(snap.TakenUtc, back.TakenUtc);
        Assert.Equal("20261015-091500.json.gz", snap.FileName);
        Assert.Equal("C", DiskSnapshot.KeyFor(@"C:\"));
        Assert.Equal("D_Games", DiskSnapshot.KeyFor(@"D:\Games"));
    }
}
