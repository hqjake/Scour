using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Programs;

namespace Scour.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;

    public MainViewModel(AppServices services)
    {
        _s = services;
        Programs = new ProgramsViewModel(services);
        Forced = new ForcedUninstallViewModel(services);
        Settings = new SettingsViewModel(services);
        Dashboard = new DashboardViewModel(services, Navigate);
        Pages =
        [
            Dashboard,
            Programs,
            new AppUpdatesViewModel(services),
            new BloatwareViewModel(services),
            new WindowsAppsViewModel(services),
            new ReinstallKitViewModel(services),
            new TracesViewModel(services),
            Forced,
            new BackupsViewModel(services),
            new JunkViewModel(services),
            new BrowserPrivacyViewModel(services),
            new PrivacyViewModel(services),
            new FileFinderViewModel(services),
            new RegistryCleanerViewModel(services),
            new SystemInfoViewModel(),
            new RepairViewModel(services),
            new AutorunViewModel(services),
            new DiskAnalyzerViewModel(services),
            new UsbViewModel(),
        ];
        Dashboard.ResolvePage = t => Pages.FirstOrDefault(p => p.GetType() == t);
        NavView = CollectionViewSource.GetDefaultView(Pages);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PageViewModel.Group)));
        _currentPage = Dashboard;
        _selectedNavPage = Dashboard;
        _ = Dashboard.ActivateAsync();
        _ = Programs.ActivateAsync(); // loads the program list other pages rely on
        Palette = new CommandPaletteViewModel(this, services);
        Settings.CheckForUpdates = () => CheckForUpdatesAsync(silent: false);
    }

    public CommandPaletteViewModel Palette { get; }

    /// <summary>Runs the tray's quick clean (set by the app).</summary>
    public Action? QuickClean { get; set; }

    // ---- Scour's own updates

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateTitle), nameof(UpdateNotes))]
    private Scour.Core.Updates.ReleaseInfo? _availableUpdate;

    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "";

    public string UpdateTitle => AvailableUpdate is { } r ? $"Scour {r.Version.ToString(3)} is available" : "";

    /// <summary>First meaningful line of the release notes.</summary>
    public string UpdateNotes => AvailableUpdate?.Notes.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).FirstOrDefault(l => l.Length > 0)
                                 ?? "";

    /// <summary>Asks GitHub for a newer release; returns a sentence describing the result.</summary>
    public async Task<string> CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release is null) return $"You're up to date — Scour {UpdateService.CurrentVersion.ToString(3)} is the latest version.";
            AvailableUpdate = release;
            UpdateStatus = "";
            if (!silent) SoundService.Play(UiSound.Scan);
            return $"Scour {release.Version.ToString(3)} is available. Use Update now in the banner at the top of the window.";
        }
        catch (Exception e)
        {
            return "Couldn't check for updates: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release || IsInstallingUpdate) return;
        if (!UpdateService.CanSelfUpdate(release))
        {
            Shell.OpenUrl(release.PageUrl);
            return;
        }
        if (!Dialogs.Confirm($"Update Scour to {release.Version.ToString(3)} now?\n\nScour downloads the new version, closes and restarts. Anything running in Scour will stop.")) return;
        IsInstallingUpdate = true;
        UpdateStatus = "Downloading…";
        try
        {
            await UpdateService.InstallAsync(release, new Progress<double>(p => { UpdateProgress = p; UpdateStatus = $"Downloading… {p:0}%"; }), CancellationToken.None);
            UpdateStatus = "Restarting…";
            (System.Windows.Application.Current as App)?.QuitForUpdate();
        }
        catch (Exception e)
        {
            UpdateStatus = "Update failed: " + e.Message;
            IsInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private void ViewReleaseNotes()
    {
        if (AvailableUpdate is { } r) Shell.OpenUrl(r.PageUrl);
    }

    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    public DashboardViewModel Dashboard { get; }
    public ProgramsViewModel Programs { get; }
    public ForcedUninstallViewModel Forced { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public ICollectionView NavView { get; }

    [ObservableProperty] private PageViewModel _currentPage;

    /// <summary>The sidebar selection; null while a page outside the list (Settings) is shown.</summary>
    [ObservableProperty] private PageViewModel? _selectedNavPage;

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        _ = value.ActivateAsync();
        SelectedNavPage = Pages.Contains(value) ? value : null;
    }

    partial void OnSelectedNavPageChanged(PageViewModel? value)
    {
        if (value is not null && value != CurrentPage) CurrentPage = value;
    }

    public void Navigate(PageViewModel page) => CurrentPage = page;

    public void Navigate<T>() where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page is not null) CurrentPage = page;
    }

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public string ThemeGlyph => ThemeService.IsDark ? "" : ""; // sun shown in dark mode, moon in light
    public string SoundGlyph => SoundService.Enabled ? "" : "";

    [RelayCommand]
    private void ToggleTheme()
    {
        _s.Settings.DarkTheme = !ThemeService.IsDark;
        _s.Settings.Save();
        ThemeService.Apply(_s.Settings.DarkTheme);
        OnPropertyChanged(nameof(ThemeGlyph));
        Settings.Refresh();
    }

    [RelayCommand]
    private void ToggleSound()
    {
        _s.Settings.SoundsEnabled = !SoundService.Enabled;
        _s.Settings.Save();
        SoundService.Enabled = _s.Settings.SoundsEnabled;
        if (SoundService.Enabled) SoundService.Play(UiSound.Success);
        OnPropertyChanged(nameof(SoundGlyph));
        Settings.Refresh();
    }

    public HuntResult? Hunt() => Hunter.Identify(_s.LastKnownPrograms);

    public void UninstallFromHunter(InstalledProgram program) => UninstallProgram(program);

    public void UninstallProgram(InstalledProgram program)
    {
        CurrentPage = Programs;
        _ = Programs.UninstallProgramAsync(program);
    }

    public void ForcedFromHunter(HuntResult hit)
    {
        if (hit.ExecutablePath is null) return;
        Forced.Prefill(hit.ExecutablePath, hit.Program);
        CurrentPage = Forced;
    }

    public void ShowInList(InstalledProgram program)
    {
        CurrentPage = Programs;
        Programs.SearchText = "";
        Programs.Filter = ProgramFilter.All;
        Programs.Selected = Programs.Programs.FirstOrDefault(p => p.Program.RegistryKey.Equals(program.RegistryKey));
    }
}
