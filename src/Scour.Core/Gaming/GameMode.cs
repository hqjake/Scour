using System.Text.Json;
using System.Text.Json.Serialization;
using Scour.Core.Platform;
using Scour.Core.Privacy;

namespace Scour.Core.Gaming;

/// <summary>What a boost does; registry boosts are applied here, the others by the app.</summary>
public enum BoostKind
{
    Registry,
    PowerPlan,
    BackgroundPriority,
}

public sealed record BoostSetting(RegKeyPath Key, string Name, RegValue Value);

/// <param name="ApplyNote">Shown when the change only takes effect later.</param>
public sealed record GameBoost(string Id, string Name, string Description, bool DefaultOn, BoostKind Kind,
                               IReadOnlyList<BoostSetting> Settings, string? ApplyNote = null);

/// <summary>A process whose priority Game mode lowered, so it can be put back (PID plus start time guards against reuse).</summary>
public sealed record LoweredProcess(int Pid, string Name, DateTime StartTimeUtc);

/// <summary>Everything needed to turn Game mode off again, even after Scour was closed or crashed.</summary>
public sealed class GameModeState
{
    public DateTime StartedUtc { get; set; }
    public List<string> Boosts { get; set; } = [];
    public List<SavedValue> Registry { get; set; } = [];
    public Guid? PreviousPowerScheme { get; set; }
    public Guid? PreviousPowerOverlay { get; set; }
    public List<LoweredProcess> Lowered { get; set; } = [];

    /// <summary>Set when Game mode switched itself on for this game; it then switches off when the game exits.</summary>
    public string? StartedForGame { get; set; }
}

/// <summary>A running process, as far as choosing what to slow down is concerned.</summary>
public sealed record ProcessCandidate(int Pid, string Name, int SessionId, bool NormalPriority);

/// <summary>
/// Game mode: a set of reversible changes that leave more of the PC to the game. Only changes that measurably
/// help are included — no "RAM cleaning" or service killing. The original values are written to a state file
/// before anything changes, so turning it off (now, or after a crash or restart) restores them exactly.
/// </summary>
public sealed class GameMode(IRegistry registry, IFileSystem fs, string stateFile)
{
    private static RegKeyPath CU(string p) => RegKeyPath.CurrentUser(p);

    public static readonly IReadOnlyList<GameBoost> All =
    [
        new("power", "Maximum performance power plan",
            "Switches to the Ultimate or High performance plan (or the Best performance power mode on laptops), so the CPU doesn't slow down to save power mid-game.",
            true, BoostKind.PowerPlan, []),
        new("windows-game-mode", "Windows Game Mode",
            "Makes sure Windows' own Game Mode is on: it gives the game priority and stops Windows Update installing drivers or nagging to restart while you play.",
            true, BoostKind.Registry,
            [
                new(CU(@"Software\Microsoft\GameBar"), "AutoGameModeEnabled", RegValue.DWord(1)),
                new(CU(@"Software\Microsoft\GameBar"), "AllowAutoGameMode", RegValue.DWord(1)),
            ]),
        new("capture", "Stop background recording",
            "Turns off Xbox Game Bar's background capture, which records the last minutes of gameplay all the time and costs frames.",
            true, BoostKind.Registry,
            [
                new(CU(@"System\GameConfigStore"), "GameDVR_Enabled", RegValue.DWord(0)),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\GameDVR"), "AppCaptureEnabled", RegValue.DWord(0)),
                new(CU(@"Software\Microsoft\Windows\CurrentVersion\GameDVR"), "HistoricalCaptureEnabled", RegValue.DWord(0)),
            ], "Takes effect for games started after turning Game mode on."),
        new("notifications", "Silence notifications",
            "Stops pop-up notifications from appearing over the game. They're still collected in the notification centre.",
            true, BoostKind.Registry,
            [new(CU(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings"), "NOC_GLOBAL_SETTING_TOASTS_ENABLED", RegValue.DWord(0))]),
        new("background", "Lower background apps",
            "Gives launchers, sync clients and updaters (Steam's web helper, Epic, OneDrive, Dropbox, Creative Cloud…) a lower priority, so they yield the CPU to the game.",
            true, BoostKind.BackgroundPriority, []),
        new("mouse", "Raw mouse movement",
            "Turns off \"Enhance pointer precision\" (mouse acceleration), so the same hand movement always turns your aim the same distance.",
            false, BoostKind.Registry,
            [
                new(CU(@"Control Panel\Mouse"), "MouseSpeed", RegValue.String("0")),
                new(CU(@"Control Panel\Mouse"), "MouseThreshold1", RegValue.String("0")),
                new(CU(@"Control Panel\Mouse"), "MouseThreshold2", RegValue.String("0")),
            ]),
    ];

    /// <summary>
    /// Background programs that are safe to slow down: nothing the player interacts with during a game
    /// (voice chat, overlays, audio and the game itself are never on this list).
    /// </summary>
    public static readonly IReadOnlySet<string> BackgroundApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EABackgroundService", "Origin", "UbisoftConnect",
        "upc", "Battle.net", "GalaxyClient", "GalaxyClient Helper", "RiotClientServices",
        "OneDrive", "Dropbox", "GoogleDriveFS", "MEGAsync", "iCloudDrive", "iCloudServices",
        "Creative Cloud", "CCXProcess", "CoreSync", "AdobeIPCBroker", "AdobeCollabSync", "AcroCEF",
        "Teams", "ms-teams", "Slack", "Skype", "Zoom", "Spotify",
        "GoogleUpdate", "MicrosoftEdgeUpdate", "OfficeClickToRun", "SearchApp", "PhoneExperienceHost", "YourPhone",
        "Widgets", "WidgetService", "msedgewebview2",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The saved state while Game mode is on; null when it is off.</summary>
    public GameModeState? Load()
    {
        try
        {
            if (fs.FileExists(stateFile)) return JsonSerializer.Deserialize<GameModeState>(fs.ReadAllText(stateFile), JsonOptions);
        }
        catch (JsonException) { /* unreadable: treat as off */ }
        return null;
    }

    public void Save(GameModeState state)
    {
        var dir = WinPath.GetDirectoryName(stateFile);
        if (dir is not null) fs.CreateDirectory(dir);
        fs.WriteAllText(stateFile, JsonSerializer.Serialize(state, JsonOptions));
    }

    public void Clear()
    {
        if (fs.FileExists(stateFile)) fs.DeleteFile(stateFile, toRecycleBin: false);
    }

    /// <summary>
    /// Saves the current values of the chosen registry boosts into <paramref name="state"/> (and to disk) and
    /// applies them. Values already recorded are kept, so applying twice never loses the originals.
    /// </summary>
    public void ApplyRegistry(GameModeState state, IEnumerable<GameBoost> boosts)
    {
        var settings = boosts.Where(b => b.Kind == BoostKind.Registry).SelectMany(b => b.Settings).ToList();
        foreach (var s in settings)
        {
            var id = s.Key.ToString();
            if (!state.Registry.Any(v => v.Key == id && v.Name == s.Name))
                state.Registry.Add(SavedValue.From(s.Key, s.Name, registry.GetValue(s.Key, s.Name)));
        }
        Save(state); // originals reach the disk before anything is changed
        foreach (var s in settings) registry.SetValue(s.Key, s.Name, s.Value);
    }

    /// <summary>Puts every saved registry value back as it was (removing the ones that didn't exist).</summary>
    public List<string> RestoreRegistry(GameModeState state)
    {
        var errors = new List<string>();
        foreach (var v in state.Registry)
        {
            try
            {
                var key = RegKeyPath.Parse(v.Key);
                if (v.ToRegValue() is { } original) registry.SetValue(key, v.Name, original);
                else registry.DeleteValue(key, v.Name);
            }
            catch (Exception e)
            {
                errors.Add($"{v.Name}: {e.Message}");
            }
        }
        return errors;
    }

    /// <summary>Which running processes "Lower background apps" slows down: listed apps in the player's session, at normal priority.</summary>
    public static List<ProcessCandidate> ToLower(IEnumerable<ProcessCandidate> running, int sessionId) =>
        running.Where(p => p.SessionId == sessionId && p.NormalPriority && BackgroundApps.Contains(p.Name)).ToList();

    /// <summary>True if the mouse boost is among the chosen ones (it needs a live settings refresh).</summary>
    public static bool ChangesMouse(IEnumerable<string> boostIds) => boostIds.Contains("mouse");
}
