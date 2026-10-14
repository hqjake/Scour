using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Privacy;

namespace Scour.App.ViewModels;

public sealed partial class TweakViewModel : ObservableObject
{
    private readonly PrivacyTweaks _engine;
    private bool _suppress;

    public TweakViewModel(PrivacyTweak tweak, PrivacyTweaks engine)
    {
        Tweak = tweak;
        _engine = engine;
        Refresh();
    }

    public PrivacyTweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public string? Note => Tweak.Note;
    public bool Recommended => Tweak.Recommended;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _stateText = "";

    public event Action<string>? Failed;

    public void Refresh()
    {
        var state = _engine.GetState(Tweak);
        _suppress = true;
        IsOn = state == TweakState.On;
        _suppress = false;
        StateText = state switch
        {
            TweakState.On => "Private",
            TweakState.Partial => "Partly applied",
            _ => "Windows default",
        };
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_suppress) return;
        try
        {
            if (value) _engine.Apply(Tweak);
            else _engine.Undo(Tweak);
        }
        catch (Exception e)
        {
            Failed?.Invoke($"{Tweak.Name}: {e.Message}");
        }
        Refresh();
    }
}

public sealed partial class PrivacyViewModel : PageViewModel
{
    private readonly PrivacyTweaks _engine;

    public PrivacyViewModel(AppServices services)
    {
        _engine = new PrivacyTweaks(services.Registry, services.FileSystem, Path.Combine(services.Folders.AppDataDir, "privacy-backup.json"));
        View = CollectionViewSource.GetDefaultView(Tweaks);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakViewModel.Category)));
    }

    public override string Title => "Privacy tweaks";
    public override string Glyph => "";
    public override string Group => "Cleaning";

    public ObservableCollection<TweakViewModel> Tweaks { get; } = [];
    public ICollectionView View { get; }

    protected override Task LoadAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        Tweaks.Clear();
        foreach (var t in PrivacyTweaks.All)
        {
            var vm = new TweakViewModel(t, _engine);
            vm.Failed += m => { StatusText = m; SoundService.Play(UiSound.Warning); };
            Tweaks.Add(vm);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int on = Tweaks.Count(t => t.IsOn);
        StatusText = $"{on} of {Tweaks.Count} protections on. Every change is saved first and can be switched back.";
    }

    [RelayCommand]
    private void ApplyRecommended()
    {
        foreach (var t in Tweaks.Where(t => t.Recommended && !t.IsOn)) t.IsOn = true;
        UpdateSummary();
        SoundService.Play(UiSound.Success);
    }

    [RelayCommand]
    private void UndoAll()
    {
        if (!Dialogs.Confirm("Switch every privacy tweak back to how it was before Scour changed it?")) return;
        foreach (var t in Tweaks.Where(t => t.IsOn || t.StateText == "Partly applied")) t.IsOn = false;
        Reload();
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { /* already gone */ }
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
