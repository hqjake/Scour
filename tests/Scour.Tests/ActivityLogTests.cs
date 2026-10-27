using Scour.Core.Removal;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class ActivityLogTests
{
    private const string Path = @"C:\ProgramData\Scour\history.jsonl";

    [Fact]
    public void Records_newest_first_and_marks_undone()
    {
        var fs = new InMemoryFileSystem();
        var log = new ActivityLog(fs, Path);
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = log.Record("Cleaning", "Cleaned junk", "1.2 GB", nowUtc: t);
        var b = log.Record("Registry", "Fixed 3 problems", null, new UndoInfo("registry-backup", "20261001-000100-x"), t.AddMinutes(1));

        var list = log.List();
        Assert.Equal([b.Id, a.Id], list.Select(e => e.Id));
        Assert.True(list[0].CanUndo);
        Assert.False(list[1].CanUndo);

        log.MarkUndone(b.Id);
        var again = new ActivityLog(fs, Path).List(); // survives a restart
        Assert.Equal(2, again.Count);
        Assert.True(again[0].Undone);
        Assert.False(again[0].CanUndo);
    }

    [Fact]
    public void A_damaged_line_does_not_lose_the_rest()
    {
        var fs = new InMemoryFileSystem();
        var log = new ActivityLog(fs, Path);
        log.Record("Cleaning", "First");
        fs.WriteAllText(Path, fs.ReadAllText(Path) + "{\"Id\":\"broken\n");
        log.Record("Cleaning", "Second");
        Assert.Equal(["Second", "First"], log.List().Select(e => e.Title).OrderByDescending(x => x));
    }

    [Fact]
    public void Old_entries_are_trimmed()
    {
        var fs = new InMemoryFileSystem();
        var log = new ActivityLog(fs, Path);
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 4005; i++) log.Record("x", "Entry " + i, nowUtc: t.AddSeconds(i));
        var list = log.List();
        Assert.True(list.Count <= 4000);
        Assert.Equal("Entry 4004", list[0].Title);
    }
}
