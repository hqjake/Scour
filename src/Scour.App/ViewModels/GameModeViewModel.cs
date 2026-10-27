using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
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

public sealed partial class GameModeViewModel : PageViewModel
{
    private readonly AppServices _s;

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
        Refresh();
    }

    public override string Title => "Game mode";
    public override string Glyph => "";
    public override string Group => "Home";

    public ObservableCollection<BoostItemViewModel> Boosts { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanChooseBoosts), nameof(ToggleText))] private bool _isOn;
    [ObservableProperty] private string _onSinceText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleCommand))] private bool _isBusy;

    public bool CanChooseBoosts => !IsOn;
    public string ToggleText => IsOn ? "Turn off Game mode" : "Turn on Game mode";
    public string HotkeyText => GameModeHotkey.Description;

    /// <summary>Raised after Game mode was switched (tray menu and sidebar follow it).</summary>
    public event Action<bool>? Changed;

    private void Refresh()
    {
        var state = _s.GameMode.State;
        IsOn = state is not null;
        OnSinceText = state is null ? "" : $"On since {state.StartedUtc.ToLocalTime():t}";
        if (state is not null)
            foreach (var b in Boosts) b.IsChecked = state.Boosts.Contains(b.Boost.Id);
    }

    private void SaveChoices()
    {
        if (IsOn) return; // the checkboxes show what is active then, not the saved choice
        _s.Settings.GameModeBoosts = Boosts.Where(b => b.IsChecked).Select(b => b.Boost.Id).ToList();
        _s.Settings.Save();
    }

    private bool CanToggle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    public async Task ToggleAsync()
    {
        IsBusy = true;
        Log.Clear();
        try
        {
            if (!IsOn)
            {
                var ids = Boosts.Where(b => b.IsChecked).Select(b => b.Boost.Id).ToList();
                if (ids.Count == 0)
                {
                    StatusText = "Choose at least one boost first.";
                    return;
                }
                var lines = await Task.Run(() => _s.GameMode.Enable(ids));
                foreach (var l in lines) Log.Add(l);
                StatusText = "Game mode is on. Have a good game!";
                SoundService.Play(UiSound.Success);
            }
            else
            {
                var errors = await Task.Run(_s.GameMode.Disable);
                foreach (var e in errors) Log.Add(e);
                StatusText = errors.Count == 0 ? "Game mode is off; everything is back as it was." : "Game mode is off, but some settings couldn't be put back (see below).";
                SoundService.Play(errors.Count == 0 ? UiSound.Click : UiSound.Warning);
            }
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
}
