using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.App.Views;
using Scour.Core.Gaming;

namespace Scour.App.ViewModels;

public sealed partial class BoostItemViewModel(GameBoost boost, bool isChecked) : ObservableObject
{
    public GameBoost Boost { get; } = boost;
    public string Name => Boost.Name;
    public string Description => Boost.Description;
    public string? Note => Boost.ApplyNote;

    [ObservableProperty] private bool _isChecked = isChecked;
}

/// <summary>An installed game with its own Game mode choices.</summary>
public sealed partial class GameItemViewModel : ObservableObject
{
    private readonly GameProfile _profile;
    private readonly Action _save;

    public GameItemViewModel(GameInfo game, GameProfile profile, Action save)
    {
        Game = game;
        _profile = profile;
        _save = save;
        _auto = profile.Auto;
        _useOwnBoosts = profile.Boosts is not null;
        foreach (var b in GameMode.All)
        {
            var item = new BoostItemViewModel(b, profile.Boosts?.Contains(b.Id) ?? b.DefaultOn);
            item.PropertyChanged += (_, _) => SaveBoosts();
            Boosts.Add(item);
        }
    }

    public GameInfo Game { get; }
    public string Name => Game.Name;
    public string Details => $"{Game.Store} · {Game.InstallDir}";
    public bool IsCustom => Game.Id.StartsWith("folder:", StringComparison.Ordinal);
    public ObservableCollection<BoostItemViewModel> Boosts { get; } = [];

    [ObservableProperty] private bool _auto;
    [ObservableProperty] private bool _useOwnBoosts;

    /// <summary>The boosts to use for this game, or null for the page's default choice.</summary>
    public List<string>? OwnBoosts => UseOwnBoosts ? Boosts.Where(b => b.IsChecked).Select(b => b.Boost.Id).ToList() : null;

    partial void OnAutoChanged(bool value)
    {
        _profile.Auto = value;
        _save();
    }

    partial void OnUseOwnBoostsChanged(bool value) => SaveBoosts();

    private void SaveBoosts()
    {
        _profile.Boosts = OwnBoosts;
        _save();
    }
}

public sealed partial class GameModeViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly GameWatcher _watcher;

    public GameModeViewModel(AppServices services)
    {
        _s = services;
        var chosen = services.Settings.GameModeBoosts;
        foreach (var b in GameMode.All)
        {
            var item = new BoostItemViewModel(b, chosen?.Contains(b.Id) ?? b.DefaultOn);
            item.PropertyChanged += (_, _) => SaveChoices();
            Boosts.Add(item);
        }
        _autoGameMode = services.Settings.AutoGameMode;
        _watcher = new GameWatcher(FindGames);
        _watcher.GameStarted += OnGameStarted;
        _watcher.GameStopped += OnGameStopped;
        _overlayWithGameMode = services.Settings.OverlayWithGameMode;
        _overlayCorner = services.Settings.OverlayCorner;
        services.Overlay.Changed += on => OnPropertyChanged(nameof(OverlayVisible));
        Refresh();
    }

    public override string Title => "Game mode";
    public override string Glyph => "";
    public override string Group => "Home";

    public ObservableCollection<BoostItemViewModel> Boosts { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanChooseBoosts), nameof(ToggleText))] private bool _isOn;
    [ObservableProperty] private string _onSinceText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleCommand))] private bool _isBusy;
    [ObservableProperty] private bool _autoGameMode;
    [ObservableProperty] private string _gamesStatus = "";

    public bool CanChooseBoosts => !IsOn;
    public string ToggleText => IsOn ? "Turn off Game mode" : "Turn on Game mode";
    public string HotkeyText => GlobalHotkeys.GameModeShortcut;
    public string OverlayHotkeyText => GlobalHotkeys.OverlayShortcut;
    public OverlayCorner[] OverlayCorners { get; } = Enum.GetValues<OverlayCorner>();

    [ObservableProperty] private bool _overlayWithGameMode;
    [ObservableProperty] private OverlayCorner _overlayCorner;

    /// <summary>The in-game overlay (FPS, CPU, GPU, RAM) is on screen.</summary>
    public bool OverlayVisible
    {
        get => _s.Overlay.IsVisible;
        set
        {
            if (value) _s.Overlay.Show();
            else _s.Overlay.Hide();
        }
    }

    partial void OnOverlayWithGameModeChanged(bool value)
    {
        _s.Settings.OverlayWithGameMode = value;
        _s.Settings.Save();
    }

    partial void OnOverlayCornerChanged(OverlayCorner value)
    {
        _s.Settings.OverlayCorner = value;
        _s.Settings.Save();
        _s.Overlay.Corner = value;
    }

    /// <summary>Raised after Game mode was switched (tray menu and sidebar follow it).</summary>
    public event Action<bool>? Changed;

    /// <summary>Starts watching for games if the user asked for that (called once the app is up).</summary>
    public void StartWatching()
    {
        if (AutoGameMode) _watcher.Start();
    }

    public void StopWatching() => _watcher.Stop();

    protected override async Task LoadAsync()
    {
        IsLoading = true;
        try { await RescanGamesAsync(); }
        finally { IsLoading = false; }
    }

    private void Refresh()
    {
        var state = _s.GameMode.State;
        IsOn = state is not null;
        var game = state?.StartedForGame is { } id ? Games.FirstOrDefault(g => g.Game.Id == id)?.Name ?? "a game" : null;
        OnSinceText = state is null ? "" : $"On since {state.StartedUtc.ToLocalTime():t}" + (game is null ? "." : $", for {game}.");
        if (state is not null)
            foreach (var b in Boosts) b.IsChecked = state.Boosts.Contains(b.Boost.Id);
    }

    private void SaveChoices()
    {
        if (IsOn) return; // the checkboxes show what is active then, not the saved choice
        _s.Settings.GameModeBoosts = DefaultBoosts();
        _s.Settings.Save();
    }

    private List<string> DefaultBoosts() => Boosts.Where(b => b.IsChecked).Select(b => b.Boost.Id).ToList();

    private bool CanToggle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    public Task ToggleAsync() => IsOn ? SwitchOffAsync() : SwitchOnAsync(DefaultBoosts(), null);

    private async Task SwitchOnAsync(List<string> ids, GameInfo? game)
    {
        if (ids.Count == 0)
        {
            StatusText = "Choose at least one boost first.";
            return;
        }
        await Run(async () =>
        {
            var lines = await Task.Run(() => _s.GameMode.Enable(ids, game?.Id));
            ChangeHistory.Record("Game mode", game is null ? "Turned Game mode on" : $"Game mode switched on for {game.Name}", string.Join(" ", lines),
                                 new Scour.Core.Removal.UndoInfo(ChangeHistory.GameModeOn, ""));
            foreach (var l in lines) Log.Add(l);
            StatusText = game is null ? "Game mode is on. Have a good game!" : $"Game mode switched on for {game.Name}. It switches off when the game closes.";
            if (OverlayWithGameMode) _s.Overlay.Show();
            SoundService.Play(UiSound.Success);
        });
    }

    private Task SwitchOffAsync() => Run(async () =>
    {
        var errors = await Task.Run(_s.GameMode.Disable);
        ChangeHistory.Record("Game mode", "Turned Game mode off", errors.Count == 0 ? "Every setting was put back." : string.Join(" ", errors));
        foreach (var e in errors) Log.Add(e);
        StatusText = errors.Count == 0 ? "Game mode is off; everything is back as it was." : "Game mode is off, but some settings couldn't be put back (see below).";
        if (OverlayWithGameMode) _s.Overlay.Hide();
        SoundService.Play(errors.Count == 0 ? UiSound.Click : UiSound.Warning);
    });

    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Log.Clear();
        try
        {
            await action();
        }
        catch (Exception e)
        {
            ErrorLog.Write("Switching Game mode", e);
            StatusText = "Couldn't switch Game mode: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
        finally
        {
            IsBusy = false;
            Refresh();
            Changed?.Invoke(IsOn);
        }
    }

    // ---------------------------------------------------------------- automatic Game mode

    partial void OnAutoGameModeChanged(bool value)
    {
        _s.Settings.AutoGameMode = value;
        _s.Settings.Save();
        if (value) _watcher.Start();
        else _watcher.Stop();
        GamesStatus = value
            ? (_s.Settings.KeepInTray ? "Watching for games." : "Watching for games while Scour is open. Turn on \"Keep running in the notification area\" in Settings so it works after you close the window.")
            : "";
    }

    private void OnGameStarted(GameInfo game)
    {
        if (!AutoGameMode || IsOn || IsBusy) return; // already on (by hand or for another game): leave it alone
        var profile = _s.Settings.GameProfiles.GetValueOrDefault(game.Id);
        if (profile is { Auto: false }) return;
        SwitchOnAsync(profile?.Boosts ?? _s.Settings.GameModeBoosts ?? DefaultBoosts(), game).Forget("Switching Game mode on for " + game.Name);
    }

    private void OnGameStopped(GameInfo game)
    {
        // Only undo what was switched on automatically for this game; a manual Game mode stays until turned off.
        if (_s.GameMode.State?.StartedForGame != game.Id) return;
        SwitchOffAsync().Forget("Switching Game mode off after " + game.Name);
    }

    private List<GameInfo> FindGames()
    {
        var drives = DriveInfo.GetDrives().Where(d =>
        {
            try { return d.DriveType == DriveType.Fixed && d.IsReady; }
            catch { return false; }
        }).Select(d => d.RootDirectory.FullName).ToList();
        return new GameLibrary(_s.Registry, _s.FileSystem, _s.Folders).Find(drives, _s.Settings.CustomGameFolders);
    }

    [RelayCommand]
    private async Task RescanGamesAsync()
    {
        var games = await Task.Run(FindGames);
        Games.Clear();
        foreach (var g in games)
        {
            if (!_s.Settings.GameProfiles.TryGetValue(g.Id, out var profile)) profile = new GameProfile();
            Games.Add(new GameItemViewModel(g, profile, () => SaveProfile(g.Id, profile)));
        }
        _watcher.Invalidate();
        GamesStatus = games.Count == 0
            ? "No games found. Add a game's folder if it came from a store Scour doesn't know."
            : $"{Format.Plural(games.Count, "game")} found." + (AutoGameMode ? " Watching for them." : "");
        Refresh();
    }

    private void SaveProfile(string id, GameProfile profile)
    {
        // Only games with non-default choices are stored.
        if (profile.Auto && profile.Boosts is null) _s.Settings.GameProfiles.Remove(id);
        else _s.Settings.GameProfiles[id] = profile;
        _s.Settings.Save();
    }

    [RelayCommand]
    private async Task AddGameFolderAsync()
    {
        var folder = Dialogs.PickFolder("Choose the folder a game is installed in");
        if (folder is null) return;
        var root = Path.GetPathRoot(folder);
        if (string.Equals(folder.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            folder.StartsWith(_s.Folders.WindowsDir, StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Error("Choose the game's own folder, not a whole drive or Windows.");
            return;
        }
        if (!_s.Settings.CustomGameFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            _s.Settings.CustomGameFolders.Add(folder);
            _s.Settings.Save();
        }
        await RescanGamesAsync();
    }

    [RelayCommand]
    private async Task RemoveGameFolderAsync(GameItemViewModel? game)
    {
        if (game is not { IsCustom: true }) return;
        _s.Settings.CustomGameFolders.RemoveAll(f => string.Equals(f, game.Game.InstallDir, StringComparison.OrdinalIgnoreCase));
        _s.Settings.GameProfiles.Remove(game.Game.Id);
        _s.Settings.Save();
        await RescanGamesAsync();
    }
}
