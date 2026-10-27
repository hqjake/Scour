using Scour.Core.Gaming;
using Scour.Core.Platform;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class GameModeTests
{
    private const string StateFile = @"C:\ProgramData\Scour\game-mode.json";
    private const string GameBar = @"HKCU\Software\Microsoft\GameBar";
    private const string Mouse = @"HKCU\Control Panel\Mouse";

    private static GameBoost Boost(string id) => GameMode.All.Single(b => b.Id == id);

    [Fact]
    public void Turning_off_restores_every_original_value_even_from_a_new_instance()
    {
        var reg = new InMemoryRegistry()
            .Set(GameBar, "AutoGameModeEnabled", RegValue.DWord(0))
            .Set(Mouse, "MouseSpeed", "1").Set(Mouse, "MouseThreshold1", "6").Set(Mouse, "MouseThreshold2", "10");
        var fs = new InMemoryFileSystem();
        var engine = new GameMode(reg, fs, StateFile);

        var state = new GameModeState { StartedUtc = DateTime.UtcNow };
        engine.ApplyRegistry(state, [Boost("windows-game-mode"), Boost("mouse")]);
        Assert.Equal(1L, reg.GetInteger(RegKeyPath.Parse(GameBar), "AutoGameModeEnabled"));
        Assert.Equal("0", reg.GetString(RegKeyPath.Parse(Mouse), "MouseSpeed"));

        // Applying again (e.g. after a crash) must not overwrite the saved originals with Game mode's values.
        engine.ApplyRegistry(engine.Load()!, [Boost("windows-game-mode"), Boost("mouse")]);

        // As after a restart: a new engine reads the state file.
        var again = new GameMode(reg, fs, StateFile);
        var loaded = again.Load();
        Assert.NotNull(loaded);
        Assert.Empty(again.RestoreRegistry(loaded));
        again.Clear();

        Assert.Equal(0L, reg.GetInteger(RegKeyPath.Parse(GameBar), "AutoGameModeEnabled"));
        Assert.Null(reg.GetValue(RegKeyPath.Parse(GameBar), "AllowAutoGameMode")); // didn't exist before
        Assert.Equal(RegValue.String("1"), reg.GetValue(RegKeyPath.Parse(Mouse), "MouseSpeed"));
        Assert.Equal(RegValue.String("10"), reg.GetValue(RegKeyPath.Parse(Mouse), "MouseThreshold2"));
        Assert.Null(again.Load());
    }

    [Fact]
    public void Originals_are_on_disk_before_anything_changes()
    {
        var reg = new InMemoryRegistry();
        var fs = new InMemoryFileSystem();
        var engine = new GameMode(reg, fs, StateFile);
        engine.ApplyRegistry(new GameModeState(), [Boost("notifications")]);
        var saved = engine.Load()!.Registry.Single();
        Assert.Equal("NOC_GLOBAL_SETTING_TOASTS_ENABLED", saved.Name);
        Assert.False(saved.Existed);
    }

    [Fact]
    public void Only_listed_background_apps_in_the_players_session_are_slowed_down()
    {
        ProcessCandidate[] running =
        [
            new(1, "steamwebhelper", 1, true),
            new(2, "OneDrive", 1, true),
            new(3, "OneDrive", 0, true),            // another session (a service)
            new(4, "Dropbox", 1, false),            // priority already changed by someone
            new(5, "eldenring", 1, true),            // the game
            new(6, "Discord", 1, true),              // voice chat
            new(7, "audiodg", 1, true),
        ];
        Assert.Equal([1, 2], GameMode.ToLower(running, sessionId: 1).Select(p => p.Pid));
    }

    [Fact]
    public void Boost_catalog_is_well_formed()
    {
        Assert.Equal(GameMode.All.Count, GameMode.All.Select(b => b.Id).Distinct().Count());
        Assert.All(GameMode.All.Where(b => b.Kind == BoostKind.Registry), b => Assert.NotEmpty(b.Settings));
        Assert.All(GameMode.All.SelectMany(b => b.Settings), s => Assert.Equal(RegHive.CurrentUser, s.Key.Hive));
        Assert.DoesNotContain("Discord", GameMode.BackgroundApps);
    }
}
