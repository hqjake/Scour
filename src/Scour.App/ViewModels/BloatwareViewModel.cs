using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Bloat;
using Scour.Core.Privacy;

namespace Scour.App.ViewModels;

public sealed partial class BloatItemViewModel(BloatMatch match) : ObservableObject
{
    public BloatMatch Match { get; } = match;
    public string Name => Match.Entry.Name;
    public string Description => Match.Entry.Description;
    public string Category => Match.Entry.Category;
    public string AdviceText => Match.Entry.Advice == BloatAdvice.Safe ? "Safe to remove" : "Optional";
    public bool IsSafe => Match.Entry.Advice == BloatAdvice.Safe;
    public string Kind => Match.Program is null ? "Store app" : "Desktop program";

    [ObservableProperty] private bool _isChecked = match.Entry.Advice == BloatAdvice.Safe;
    [ObservableProperty] private string _status = "";
}

public sealed partial class BloatwareViewModel : PageViewModel
{
    private readonly AppServices _s;

    public BloatwareViewModel(AppServices services)
    {
        _s = services;
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BloatItemViewModel.Category)));
    }

    public override string Title => "Bloatware remover";
    public override string Glyph => "\uE71C";
    public override string Group => "Programs";

    public ObservableCollection<BloatItemViewModel> Items { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private bool _allUsers;
    [ObservableProperty] private bool _deprovision = true;
    [ObservableProperty] private bool _reinstallBlocked;

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Looking for pre-installed apps…";
        try
        {
            var appx = await AppxService.ListAsync();
            var matches = BloatCatalog.Find(appx.Select(a => new AppxRef(a.Name, a.FullName, a.FamilyName)), _s.LastKnownPrograms);
            Items.Clear();
            foreach (var m in matches) Items.Add(new BloatItemViewModel(m));
            var tweak = PrivacyTweaks.All.Single(t => t.Id == "silent-apps");
            ReinstallBlocked = Tweaks().GetState(tweak) == TweakState.On;
            StatusText = Items.Count == 0 ? "No bloatware found — this PC is clean." :
                $"Found {Format.Plural(Items.Count, "pre-installed app")}; {Items.Count(i => i.IsSafe)} {(Items.Count(i => i.IsSafe) == 1 ? "is" : "are")} safe to remove.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private PrivacyTweaks Tweaks() => new(_s.Registry, _s.FileSystem, Path.Combine(_s.Folders.AppDataDir, "privacy-backup.json"));

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var i in Items) i.IsChecked = i.IsSafe;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsChecked = false;
    }

    [RelayCommand]
    private void BlockReinstall()
    {
        Tweaks().Apply(PrivacyTweaks.All.Single(t => t.Id == "silent-apps"));
        ReinstallBlocked = true;
        StatusText = "Windows will no longer silently install suggested apps.";
        SoundService.Play(UiSound.Success);
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var chosen = Items.Where(i => i.IsChecked).ToList();
        if (chosen.Count == 0) return;
        var store = chosen.Where(i => i.Match.Program is null).ToList();
        var desktop = chosen.Where(i => i.Match.Program is not null).ToList();
        var msg = $"Remove {Format.Plural(chosen.Count, "app")}?\n\n" + string.Join("\n", chosen.Take(12).Select(c => "  • " + c.Name)) +
                  (chosen.Count > 12 ? $"\n  …and {chosen.Count - 12} more" : "") +
                  "\n\nStore apps can be reinstalled from the Microsoft Store at any time." +
                  (desktop.Count > 0 ? "\nDesktop programs are removed with their own uninstaller, then their leftovers are cleaned." : "");
        if (!Dialogs.Confirm(msg)) return;

        IsLoading = true;
        try
        {
            if (store.Count > 0)
            {
                StatusText = $"Removing {Format.Plural(store.Count, "Store app")}…";
                foreach (var i in store) i.Status = "Removing…";
                var packages = store.SelectMany(i => i.Match.Packages).ToList();
                var results = await AppxService.RemoveManyAsync(packages.Select(p => (p.Name, p.FullName)), AllUsers, Deprovision);
                foreach (var i in store)
                {
                    var errors = i.Match.Packages.Select(p => results.GetValueOrDefault(p.Name)).Where(e => e is not null).ToList();
                    i.Status = errors.Count == 0 ? "Removed" : errors[0]!;
                    if (errors.Count == 0)
                        foreach (var p in i.Match.Packages)
                        {
                            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", p.FamilyName);
                            try { if (Directory.Exists(data)) _s.FileSystem.DeleteDirectory(data, _s.Settings.FilesToRecycleBin); }
                            catch { /* in use; harmless */ }
                        }
                }
            }
            if (desktop.Count > 0)
            {
                var jobs = desktop.Select(d => new UninstallJob { Title = d.Match.Program!.DisplayName, Program = d.Match.Program, RunUninstaller = true }).ToList();
                Dialogs.ShowUninstallWizard(new UninstallWizardViewModel(_s, jobs, quick: true));
                _s.NotifyProgramsChanged();
            }
            int removed = store.Count(i => i.Status == "Removed");
            StatusText = $"Removed {Format.Plural(removed, "Store app")}" + (store.Count > removed ? $"; {store.Count - removed} could not be removed (see status)." : ".");
            foreach (var i in store.Where(i => i.Status == "Removed")) Items.Remove(i);
            SoundService.Play(removed == store.Count ? UiSound.Success : UiSound.Warning);
        }
        finally
        {
            IsLoading = false;
        }
        if (store.Count > 0 || desktop.Count > 0) await RefreshAsync();
    }
}
