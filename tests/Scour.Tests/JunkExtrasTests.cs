using Microsoft.Data.Sqlite;
using Scour.Core.Junk;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class JunkExtrasTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("scour-tests").FullName;
    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private static void Sql(string db, params string[] statements)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        foreach (var s in statements)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
    }

    private static long Count(string db, string table)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Finds_chromium_and_firefox_profiles()
    {
        var f = TestFolders.Create();
        var fs = new InMemoryFileSystem()
            .File(f.LocalAppData + @"\Google\Chrome\User Data\Default\Preferences")
            .File(f.LocalAppData + @"\Google\Chrome\User Data\Profile 2\History")
            .Dir(f.LocalAppData + @"\Google\Chrome\User Data\System Profile")
            .Dir(f.LocalAppData + @"\Google\Chrome\User Data\Crashpad")
            .File(f.RoamingAppData + @"\Opera Software\Opera Stable\History")
            .File(f.RoamingAppData + @"\Mozilla\Firefox\Profiles\ab12cd.default-release\places.sqlite");
        var profiles = new BrowserPrivacy(fs, f).FindProfiles();
        Assert.Equal(["Google Chrome — Default", "Google Chrome — Profile 2", "Mozilla Firefox — default-release", "Opera — Default"],
                     profiles.Select(p => p.DisplayName).OrderBy(n => n));
    }

    [Fact]
    public void Chromium_history_clear_keeps_unknown_tables_and_ignores_missing_ones()
    {
        var db = Path.Combine(_tmp, "History");
        Sql(db, "CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT)", "CREATE TABLE visits(id INTEGER PRIMARY KEY, url INT)",
                "CREATE TABLE downloads(id INTEGER PRIMARY KEY)", "CREATE TABLE meta(key TEXT, value TEXT)",
                "INSERT INTO urls VALUES (1,'https://a'),(2,'https://b')", "INSERT INTO visits VALUES (1,1)",
                "INSERT INTO downloads VALUES (1)", "INSERT INTO meta VALUES ('version','70')");
        BrowserPrivacy.Exec(db, "visits", "urls", "downloads", "clusters");
        Assert.Equal(0, Count(db, "urls"));
        Assert.Equal(0, Count(db, "downloads"));
        Assert.Equal(1, Count(db, "meta"));
    }

    [Fact]
    public void Firefox_history_clear_keeps_bookmarked_places()
    {
        var db = Path.Combine(_tmp, "places.sqlite");
        Sql(db,
            "CREATE TABLE moz_places(id INTEGER PRIMARY KEY, url TEXT, visit_count INT, last_visit_date INT, frecency INT)",
            "CREATE TABLE moz_historyvisits(id INTEGER PRIMARY KEY, place_id INT)",
            "CREATE TABLE moz_inputhistory(place_id INT, input TEXT)",
            "CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, fk INT)",
            "CREATE TABLE moz_annos(id INTEGER PRIMARY KEY, place_id INT)",
            "INSERT INTO moz_places VALUES (1,'https://bookmarked',5,1,100),(2,'https://visited',3,1,50)",
            "INSERT INTO moz_historyvisits VALUES (1,1),(2,2)", "INSERT INTO moz_bookmarks VALUES (1,1),(2,NULL)",
            "INSERT INTO moz_annos VALUES (1,2)");
        var profile = new BrowserProfile("Mozilla Firefox", "x", _tmp, BrowserEngine.Firefox, "firefox");
        var fs = new InMemoryFileSystem();
        var result = new BrowserPrivacy(fs, TestFolders.Create()).Clear(profile, PrivacyItems.History,
            p => p.Replace('\\', Path.DirectorySeparatorChar));
        Assert.Empty(result.Errors);
        Assert.Equal(PrivacyItems.History, result.Cleared);
        Assert.Equal(1, Count(db, "moz_places"));
        Assert.Equal(0, Count(db, "moz_historyvisits"));
        Assert.Equal(0, Count(db, "moz_annos"));
        Assert.Equal(2, Count(db, "moz_bookmarks"));
    }

    [Fact]
    public void Finds_duplicates_by_content_not_just_size()
    {
        byte[] Data(byte fill, int size = 200_000) => Enumerable.Repeat(fill, size).ToArray();
        var a = Data(1);
        var b = Data(1); b[150_000] = 9; // same size and same first 64 KB, different content
        var fs = new InMemoryFileSystem()
            .File(@"D:\Photos\one.jpg", a)
            .File(@"D:\Backup\one (copy).jpg", a)
            .File(@"D:\Backup\Old\one.jpg", a)
            .File(@"D:\Backup\near-copy.jpg", b)
            .File(@"D:\Small\tiny1.txt", Data(5, 10))
            .File(@"D:\Small\tiny2.txt", Data(5, 10))
            .File(@"D:\Backup\.git\objects\one", a);
        var groups = new FileFinders(fs).FindDuplicates([@"D:\"], new FinderOptions { MinSize = 1000 });
        var g = Assert.Single(groups);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(400_000, g.Wasted);
        Assert.DoesNotContain(g.Files, f => f.Path.Contains("near-copy") || f.Path.Contains(".git"));
    }

    [Fact]
    public void Finds_large_old_files_sorted_by_size()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var fs = new InMemoryFileSystem()
            .File(@"D:\a.iso", 5000, now.AddYears(-2))
            .File(@"D:\x\b.zip", 9000, now.AddYears(-1))
            .File(@"D:\x\recent.mkv", 20000, now.AddDays(-1))
            .File(@"D:\small.txt", 10, now.AddYears(-3));
        var finder = new FileFinders(fs);
        var all = finder.FindLargeFiles([@"D:\"], new FinderOptions { MinSize = 1000 });
        Assert.Equal([@"D:\x\recent.mkv", @"D:\x\b.zip", @"D:\a.iso"], all.Select(f => f.Path));
        var old = finder.FindLargeFiles([@"D:\"], new FinderOptions { MinSize = 1000 }, now.AddMonths(-6));
        Assert.Equal([@"D:\x\b.zip", @"D:\a.iso"], old.Select(f => f.Path));
    }

    [Fact]
    public void Category_ids_are_unique_and_grouped()
    {
        var cats = JunkCleaner.DefaultCategories(TestFolders.Create());
        Assert.Equal(cats.Count, cats.Select(c => c.Id).Distinct().Count());
        Assert.Subset(new HashSet<string> { "Windows", "Browsers", "Applications", "Developer" }, cats.Select(c => c.Group).ToHashSet());
    }
}
