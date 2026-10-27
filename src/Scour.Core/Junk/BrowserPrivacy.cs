using Microsoft.Data.Sqlite;
using Scour.Core.Platform;

namespace Scour.Core.Junk;

public enum BrowserEngine
{
    Chromium,
    Firefox,
}

[Flags]
public enum PrivacyItems
{
    None = 0,
    History = 1,
    Cookies = 2,
    FormData = 4,
    Sessions = 8,
}

public sealed record BrowserProfile(string Browser, string ProfileName, string Path, BrowserEngine Engine, string ProcessName)
{
    public string DisplayName => $"{Browser} — {ProfileName}";
}

public sealed record PrivacyResult(BrowserProfile Profile, PrivacyItems Cleared, List<string> Errors);

/// <summary>
/// Clears browsing history, cookies, form autofill entries and saved sessions. Works on the
/// browsers' own SQLite databases so bookmarks, passwords, saved cards and settings stay intact.
/// The browser must be closed (its databases are locked while it runs).
/// </summary>
public sealed class BrowserPrivacy(IFileSystem fs, KnownFolders folders)
{
    private static readonly (string Name, string Root, bool LocalRoot, bool ProfileIsRoot, string Process)[] Chromium =
    [
        ("Google Chrome", @"Google\Chrome\User Data", true, false, "chrome"),
        ("Microsoft Edge", @"Microsoft\Edge\User Data", true, false, "msedge"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data", true, false, "brave"),
        ("Vivaldi", @"Vivaldi\User Data", true, false, "vivaldi"),
        ("Opera", @"Opera Software\Opera Stable", false, true, "opera"),
        ("Opera GX", @"Opera Software\Opera GX Stable", false, true, "opera"),
    ];

    public List<BrowserProfile> FindProfiles()
    {
        var list = new List<BrowserProfile>();
        foreach (var (name, root, local, profileIsRoot, process) in Chromium)
        {
            var dir = WinPath.Combine(local ? folders.LocalAppData : folders.RoamingAppData, root);
            if (!fs.DirectoryExists(dir)) continue;
            if (profileIsRoot)
            {
                // Older Opera keeps the profile in the root; newer builds in "Default", with extra profiles in
                // "_side_profiles".
                var main = WinPath.Combine(dir, "Default");
                list.Add(new BrowserProfile(name, "Default", IsChromiumProfile(main) ? main : dir, BrowserEngine.Chromium, process));
                var side = WinPath.Combine(dir, "_side_profiles");
                if (fs.DirectoryExists(side))
                    foreach (var p in fs.EnumerateDirectories(side).Where(IsChromiumProfile))
                        list.Add(new BrowserProfile(name, WinPath.GetFileName(p), p, BrowserEngine.Chromium, process));
                continue;
            }
            foreach (var p in fs.EnumerateDirectories(dir))
            {
                var leaf = WinPath.GetFileName(p);
                bool isProfile = leaf == "Default" || leaf.StartsWith("Profile ", StringComparison.Ordinal);
                if (isProfile && IsChromiumProfile(p))
                    list.Add(new BrowserProfile(name, leaf == "Default" ? "Default" : leaf, p, BrowserEngine.Chromium, process));
            }
        }

        foreach (var p in FirefoxProfileDirs())
            if (fs.FileExists(WinPath.Combine(p, "places.sqlite")) || fs.FileExists(WinPath.Combine(p, "cookies.sqlite")))
            {
                var leaf = WinPath.GetFileName(p);
                int dot = leaf.IndexOf('.');
                list.Add(new BrowserProfile("Mozilla Firefox", dot >= 0 ? leaf[(dot + 1)..] : leaf, p, BrowserEngine.Firefox, "firefox"));
            }
        return list;
    }

    private bool IsChromiumProfile(string dir) =>
        fs.FileExists(WinPath.Combine(dir, "Preferences")) || fs.FileExists(WinPath.Combine(dir, "History"));

    /// <summary>
    /// Firefox profile folders: the usual Profiles folder, any profile profiles.ini places elsewhere, and the
    /// Microsoft Store build's folder.
    /// </summary>
    private IEnumerable<string> FirefoxProfileDirs()
    {
        var seen = new HashSet<string>(WinPath.Comparer);
        var roots = new List<string> { WinPath.Combine(folders.RoamingAppData, @"Mozilla\Firefox") };
        var packages = WinPath.Combine(folders.LocalAppData, "Packages");
        if (fs.DirectoryExists(packages))
            roots.AddRange(fs.EnumerateDirectories(packages)
                .Where(d => WinPath.GetFileName(d).StartsWith("Mozilla.Firefox", StringComparison.OrdinalIgnoreCase))
                .Select(d => WinPath.Combine(d, @"LocalCache\Roaming\Mozilla\Firefox")));

        foreach (var root in roots.Where(fs.DirectoryExists))
        {
            var profiles = WinPath.Combine(root, "Profiles");
            if (fs.DirectoryExists(profiles))
                foreach (var p in fs.EnumerateDirectories(profiles))
                    if (seen.Add(p)) yield return p;

            var ini = WinPath.Combine(root, "profiles.ini");
            if (!fs.FileExists(ini)) continue;
            string text;
            try { text = fs.ReadAllText(ini); }
            catch { continue; }
            foreach (var p in ParseProfilesIni(text, root))
                if (fs.DirectoryExists(p) && seen.Add(p)) yield return p;
        }
    }

    /// <summary>Profile paths from Firefox's profiles.ini ("Path=Profiles/x.default", IsRelative=1 or 0).</summary>
    internal static List<string> ParseProfilesIni(string text, string root)
    {
        var result = new List<string>();
        string? path = null;
        bool relative = true;
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var p = path.Trim().Replace('/', '\\');
                result.Add(relative ? WinPath.Combine(root, p) : p);
            }
            path = null;
            relative = true;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) Flush();
            else if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) path = line[5..];
            else if (line.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase)) relative = line[11..].Trim() != "0";
        }
        Flush();
        return result;
    }

    /// <param name="toNativePath">Maps the engine's Windows path to a path the SQLite library can open (identity on Windows).</param>
    public PrivacyResult Clear(BrowserProfile profile, PrivacyItems items, Func<string, string>? toNativePath = null)
    {
        toNativePath ??= p => p;
        var errors = new List<string>();
        var cleared = PrivacyItems.None;

        void Run(PrivacyItems item, Action action)
        {
            if (!items.HasFlag(item)) return;
            try
            {
                action();
                cleared |= item;
            }
            // BUSY, LOCKED, IOERR, CANTOPEN: Chrome and Edge hold some files with an exclusive lock, and Edge
            // keeps running in the background after its window closes.
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 or 10 or 14)
            {
                errors.Add($"{item}: the database is in use — close {profile.Browser} and try again.");
            }
            catch (Exception e)
            {
                errors.Add($"{item}: {e.Message}");
            }
        }

        string P(params string[] parts) => WinPath.Combine(profile.Path, string.Join("\\", parts));

        if (profile.Engine == BrowserEngine.Chromium)
        {
            Run(PrivacyItems.History, () =>
            {
                Exec(toNativePath(P("History")),
                    "visits", "urls", "keyword_search_terms", "downloads", "downloads_url_chains", "downloads_slices",
                    "segments", "segment_usage", "visit_source", "clusters", "clusters_and_visits", "cluster_keywords", "content_annotations",
                    "context_annotations");
                DeleteFiles(P("Visited Links"), P("Top Sites"), P("Top Sites-journal"), P("Shortcuts"), P("Shortcuts-journal"));
            });
            Run(PrivacyItems.Cookies, () =>
            {
                var cookies = fs.FileExists(P("Network", "Cookies")) ? P("Network", "Cookies") : P("Cookies");
                Exec(toNativePath(cookies), "cookies");
            });
            Run(PrivacyItems.FormData, () => Exec(toNativePath(P("Web Data")), "autofill"));
            Run(PrivacyItems.Sessions, () =>
            {
                DeleteFiles(P("Current Session"), P("Current Tabs"), P("Last Session"), P("Last Tabs"));
                DeleteFolderContents(P("Sessions"));
            });
        }
        else
        {
            Run(PrivacyItems.History, () => ExecSql(toNativePath(P("places.sqlite")),
                "DELETE FROM moz_historyvisits",
                "DELETE FROM moz_inputhistory",
                "DELETE FROM moz_places WHERE id NOT IN (SELECT fk FROM moz_bookmarks WHERE fk IS NOT NULL)",
                "DELETE FROM moz_annos WHERE place_id NOT IN (SELECT id FROM moz_places)",
                "UPDATE moz_places SET visit_count = 0, last_visit_date = NULL, frecency = 0"));
            Run(PrivacyItems.Cookies, () => Exec(toNativePath(P("cookies.sqlite")), "moz_cookies"));
            Run(PrivacyItems.FormData, () => Exec(toNativePath(P("formhistory.sqlite")), "moz_formhistory"));
            Run(PrivacyItems.Sessions, () =>
            {
                DeleteFiles(P("sessionstore.jsonlz4"));
                DeleteFolderContents(P("sessionstore-backups"));
            });
        }
        return new PrivacyResult(profile, cleared, errors);
    }

    private void DeleteFiles(params string[] paths)
    {
        foreach (var p in paths)
            if (fs.FileExists(p)) fs.DeleteFile(p, toRecycleBin: false);
    }

    private void DeleteFolderContents(string dir)
    {
        if (!fs.DirectoryExists(dir)) return;
        foreach (var f in fs.EnumerateFiles(dir)) fs.DeleteFile(f, toRecycleBin: false);
    }

    /// <summary>Empties the given tables (skipping ones this browser version doesn't have).</summary>
    public static void Exec(string dbPath, params string[] tables) =>
        ExecSql(dbPath, tables.Select(t => $"DELETE FROM \"{t}\"").ToArray());

    public static void ExecSql(string dbPath, params string[] statements)
    {
        if (!File.Exists(dbPath)) return;
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            foreach (var sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                try { cmd.ExecuteNonQuery(); }
                catch (SqliteException e) when (e.Message.Contains("no such table") || e.Message.Contains("no such column")) { /* other version */ }
            }
            tx.Commit();
        }
        using (var vacuum = conn.CreateCommand())
        {
            vacuum.CommandText = "VACUUM";
            try { vacuum.ExecuteNonQuery(); } catch (SqliteException) { /* not critical */ }
        }
    }
}
