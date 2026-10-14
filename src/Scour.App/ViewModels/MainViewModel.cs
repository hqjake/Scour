using CommunityToolkit.Mvvm.ComponentModel;
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
        Pages =
        [
            Programs,
            new WindowsAppsViewModel(services),
            new TracesViewModel(services),
            Forced,
            new AutorunViewModel(services),
            new JunkViewModel(services),
            new BrowserPrivacyViewModel(services),
            new FileFinderViewModel(services),
            new BackupsViewModel(services),
        ];
        Settings = new SettingsViewModel(services);
        _currentPage = Programs;
        _ = Programs.ActivateAsync();
    }

    public ProgramsViewModel Programs { get; }
    public ForcedUninstallViewModel Forced { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _currentPage;

    partial void OnCurrentPageChanged(PageViewModel value) => _ = value.ActivateAsync();

    public bool IsElevated => Environment.IsPrivilegedProcess;

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
