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
            new WindowsAppsViewModel(services),
            new TracesViewModel(services),
            Forced,
            new BackupsViewModel(services),
            new JunkViewModel(services),
            new BrowserPrivacyViewModel(services),
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
    }

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

    public void UninstallFromHunter(InstalledProgram program)
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
