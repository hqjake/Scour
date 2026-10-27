using System.Text.Json;
using Scour.Core.Platform;

namespace Scour.Core.Gaming;

/// <summary>An installed game. <see cref="Id"/> is stable across scans ("steam:570", "folder:D:\Games\X").</summary>
public sealed record GameInfo(string Id, string Name, string Store, string InstallDir);

/// <summary>
/// Finds installed games from the launchers' own records (Steam, Epic, GOG, Ubisoft) and their usual install
/// folders (EA, Riot, Xbox), so Game mode can switch itself on when one starts.
/// </summary>
public sealed class GameLibrary(IRegistry registry, IFileSystem fs, KnownFolders folders)
{
    /// <param name="driveRoots">Fixed drives to look for Xbox and Riot folders on ("C:\", "D:\").</param>
    /// <param name="customFolders">Folders the user added as games.</param>
    public List<GameInfo> Find(IEnumerable<string> driveRoots, IEnumerable<string>? customFolders = null)
    {
        var games = new List<GameInfo>();
        void Try(Action find)
        {
            try { find(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { /* one store failing mustn't hide the rest */ }
        }
        var roots = driveRoots.ToList();
        Try(() => games.AddRange(Steam()));
        Try(() => games.AddRange(Epic()));
        Try(() => games.AddRange(Gog()));
        Try(() => games.AddRange(Ubisoft()));
        Try(() => games.AddRange(Folders("EA", WinPath.Combine(folders.ProgramFiles, "EA Games"))));
        foreach (var root in roots)
        {
            Try(() => games.AddRange(Folders("Riot", WinPath.Combine(root, "Riot Games"))));
            Try(() => games.AddRange(Folders("Xbox", WinPath.Combine(root, "XboxGames"))));
        }
        foreach (var dir in customFolders ?? [])
            if (fs.DirectoryExists(dir)) games.Add(new GameInfo("folder:" + dir.ToLowerInvariant(), WinPath.GetFileName(dir), "Added by you", dir));

        // A game can be listed twice (e.g. a custom folder that Steam also knows): keep the first.
        return games.Where(g => fs.DirectoryExists(g.InstallDir))
                    .GroupBy(g => WinPath.Normalize(g.InstallDir), WinPath.Comparer).Select(g => g.First())
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- Steam

    /// <summary>Steamworks redistributables and tools that live among the games.</summary>
    private static readonly HashSet<string> SteamNonGames = ["228980", "1070560", "1391110", "1628350", "1493710", "250820"];

    private IEnumerable<GameInfo> Steam()
    {
        var steam = (registry.GetString(RegKeyPath.CurrentUser(@"Software\Valve\Steam"), "SteamPath")
                     ?? registry.GetString(RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Valve\Steam"), "InstallPath"))?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steam)) yield break;

        var libraries = new List<string> { steam };
        var vdf = WinPath.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (fs.FileExists(vdf))
            foreach (var (_, lib) in Vdf.Parse(fs.ReadAllText(vdf)).Child("libraryfolders")?.Children ?? [])
                if (lib.Value("path") is { } p) libraries.Add(p.Replace('/', '\\'));

        foreach (var lib in libraries.Distinct(WinPath.Comparer))
        {
            var apps = WinPath.Combine(lib, "steamapps");
            if (!fs.DirectoryExists(apps)) continue;
            foreach (var file in fs.EnumerateFiles(apps))
            {
                var name = WinPath.GetFileName(file);
                if (!name.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)) continue;
                var state = Vdf.Parse(fs.ReadAllText(file)).Child("AppState");
                if (state?.Value("appid") is not { } id || SteamNonGames.Contains(id) || state.Value("installdir") is not { } dir) continue;
                yield return new GameInfo("steam:" + id, state.Value("name") ?? dir, "Steam", WinPath.Combine(apps, "common", dir));
            }
        }
    }

    // ---------------------------------------------------------------- Epic

    private IEnumerable<GameInfo> Epic()
    {
        var manifests = WinPath.Combine(folders.ProgramData, @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!fs.DirectoryExists(manifests)) yield break;
        foreach (var file in fs.EnumerateFiles(manifests).Where(f => f.EndsWith(".item", StringComparison.OrdinalIgnoreCase)))
        {
            GameInfo? game = null;
            try
            {
                using var doc = JsonDocument.Parse(fs.ReadAllText(file));
                var r = doc.RootElement;
                string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                // Unreal Engine and other tools are "applications", not games.
                bool isGame = !r.TryGetProperty("AppCategories", out var cats) || cats.ValueKind != JsonValueKind.Array ||
                              cats.EnumerateArray().Any(c => c.GetString() == "games");
                if (S("InstallLocation") is { Length: > 0 } dir && !B("bIsIncompleteInstall") && isGame)
                    game = new GameInfo("epic:" + (S("AppName") ?? dir), S("DisplayName") ?? WinPath.GetFileName(dir), "Epic", dir.Replace('/', '\\'));
            }
            catch (JsonException) { /* half-written manifest */ }
            if (game is not null) yield return game;
        }
    }

    // ---------------------------------------------------------------- GOG and Ubisoft

    private IEnumerable<GameInfo> Gog()
    {
        var root = RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        foreach (var id in registry.GetSubKeyNames(root))
        {
            var key = root.Child(id);
            if (registry.GetString(key, "path") is { Length: > 0 } dir)
                yield return new GameInfo("gog:" + id, registry.GetString(key, "gameName") ?? WinPath.GetFileName(dir), "GOG", dir);
        }
    }

    private IEnumerable<GameInfo> Ubisoft()
    {
        var root = RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        foreach (var id in registry.GetSubKeyNames(root))
            if (registry.GetString(root.Child(id), "InstallDir") is { Length: > 0 } dir)
            {
                dir = dir.Replace('/', '\\').TrimEnd('\\');
                yield return new GameInfo("ubisoft:" + id, WinPath.GetFileName(dir), "Ubisoft", dir);
            }
    }

    /// <summary>Launchers that install each game in its own folder under a known one.</summary>
    private IEnumerable<GameInfo> Folders(string store, string parent)
    {
        if (!fs.DirectoryExists(parent)) yield break;
        foreach (var dir in fs.EnumerateDirectories(parent))
        {
            var name = WinPath.GetFileName(dir);
            if (name.Equals("Riot Client", StringComparison.OrdinalIgnoreCase)) continue; // the launcher itself
            yield return new GameInfo($"{store.ToLowerInvariant()}:{name.ToLowerInvariant()}", name, store, dir);
        }
    }

    // ---------------------------------------------------------------- matching running programs

    /// <summary>Programs that ship inside game folders but aren't the game: launchers, crash reporters, anti-cheat, installers.</summary>
    private static readonly string[] HelperWords =
    [
        "crash", "report", "launcher", "unins", "setup", "redist", "vcredist", "dxsetup", "prereq", "easyanticheat", "battleye",
        "beservice", "updater", "cefsubprocess", "cefsharp", "webengine", "webhelper", "overlay", "helper", "install",
    ];

    /// <summary>The game a running program belongs to, or null if it isn't one (or is only a game's helper).</summary>
    public static GameInfo? GameFor(string? exePath, IReadOnlyList<GameInfo> games)
    {
        if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var file = WinPath.GetFileName(exePath);
        if (HelperWords.Any(w => file.Contains(w, StringComparison.OrdinalIgnoreCase))) return null;
        // The deepest folder wins, for games installed inside another game's folder.
        return games.Where(g => WinPath.IsStrictlyUnder(exePath, g.InstallDir))
                    .OrderByDescending(g => g.InstallDir.Length).FirstOrDefault();
    }
}

/// <summary>Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class Vdf
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, Vdf Node)> _children = [];

    public IReadOnlyList<(string Key, Vdf Node)> Children => _children;
    public string? Value(string key) => _values.GetValueOrDefault(key);
    public Vdf? Child(string key) => _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Node;

    public static Vdf Parse(string text)
    {
        var tokens = Tokens(text).GetEnumerator();
        var root = new Vdf();
        Fill(root, tokens);
        return root;
    }

    private enum Kind { Text, Open, Close }

    private static void Fill(Vdf node, IEnumerator<(Kind Kind, string Text)> tokens)
    {
        while (tokens.MoveNext())
        {
            var (kind, key) = tokens.Current;
            if (kind == Kind.Close) return;
            if (kind == Kind.Open) { Fill(new Vdf(), tokens); continue; } // a block without a name: skip it
            if (!tokens.MoveNext()) return;
            switch (tokens.Current.Kind)
            {
                case Kind.Text: node._values[key] = tokens.Current.Text; break;
                case Kind.Open:
                    var child = new Vdf();
                    node._children.Add((key, child));
                    Fill(child, tokens);
                    break;
                default: return; // a key with nothing after it closes the block
            }
        }
    }

    private static IEnumerable<(Kind, string)> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}')
            {
                i++;
                yield return (c == '{' ? Kind.Open : Kind.Close, "");
                continue;
            }
            var sb = new System.Text.StringBuilder();
            if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) sb.Append(s[i++]);
            }
            yield return (Kind.Text, sb.ToString());
        }
    }
}
