using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Removal;

namespace Scour.App.ViewModels;

public enum ProgramFilter
{
    All,
    RecentlyInstalled,
    Large,
    Broken,
    CurrentUser,
    ThirtyTwoBit,
}

public sealed record FilterOption(ProgramFilter Value, string Label);

public sealed partial class ProgramsViewModel : PageViewModel
{
    private readonly AppServices _s;

    public ProgramsViewModel(AppServices services)
    {
        _s = services;
        View = CollectionViewSource.GetDefaultView(Programs);
        View.Filter = o => o is ProgramItemViewModel p && PassesFilter(p);
        View.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));
        _s.ProgramsChanged += () => _ = RefreshAsync();
    }

    public override string Title => "Uninstaller";
    public override string Glyph => "";

    public ObservableCollection<ProgramItemViewModel> Programs { get; } = [];
    public ICollectionView View { get; }

    public IReadOnlyList<FilterOption> Filters { get; } =
    [
        new(ProgramFilter.All, "All programs"),
        new(ProgramFilter.RecentlyInstalled, "Installed in the last 30 days"),
        new(ProgramFilter.Large, "Larger than 500 MB"),
        new(ProgramFilter.Broken, "Missing or broken uninstaller"),
        new(ProgramFilter.CurrentUser, "Installed for current user only"),
        new(ProgramFilter.ThirtyTwoBit, "32-bit programs"),
    ];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ProgramFilter _filter = ProgramFilter.All;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(QuickUninstallCommand), nameof(OpenFolderCommand),
        nameof(OpenRegistryCommand), nameof(SearchOnlineCommand), nameof(RemoveEntryCommand), nameof(OpenHelpLinkCommand))]
    private ProgramItemViewModel? _selected;

    /// <summary>Kept in sync with the grid's multi-selection by the view.</summary>
    public IReadOnlyList<ProgramItemViewModel> SelectedItems { get; private set; } = [];

    public void SetSelection(IEnumerable<ProgramItemViewModel> items)
    {
        SelectedItems = items.ToList();
        UninstallCommand.NotifyCanExecuteChanged();
        QuickUninstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectionText));
    }

    public string SelectionText => SelectedItems.Count > 1 ? $"{SelectedItems.Count} programs selected" : "";

    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnFilterChanged(ProgramFilter value) => RefreshView();

    private void RefreshView()
    {
        View.Refresh();
        UpdateStatus();
    }

    private bool PassesFilter(ProgramItemViewModel p)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) && !p.Matches(SearchText.Trim())) return false;
        return Filter switch
        {
            ProgramFilter.RecentlyInstalled => p.InstallDate is { } d && d >= DateTime.Today.AddDays(-30),
            ProgramFilter.Large => p.SizeBytes >= 500L * 1024 * 1024,
            ProgramFilter.Broken => p.IsBroken,
            ProgramFilter.CurrentUser => p.Program.Scope == InstallScope.User,
            ProgramFilter.ThirtyTwoBit => p.Program.Is32BitEntry,
            _ => true,
        };
    }

    private void UpdateStatus()
    {
        var visible = View.Cast<ProgramItemViewModel>().ToList();
        long total = visible.Sum(p => p.SizeBytes);
        StatusText = $"{Format.Plural(visible.Count, "program")}" + (total > 0 ? $" · {Format.Bytes(total)}" : "");
    }

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var selectedKey = Selected?.Program.RegistryKey;
            var items = await Task.Run(() =>
                _s.LoadPrograms().Select(p => new ProgramItemViewModel(p, _s.Icons, UninstallerMissing(p))).ToList());
            Programs.Clear();
            foreach (var i in items) Programs.Add(i);
            if (selectedKey is not null) Selected = Programs.FirstOrDefault(p => p.Program.RegistryKey.Equals(selectedKey.Value));
            UpdateStatus();
        }
        catch (Exception e)
        {
            StatusText = "Could not read installed programs: " + e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool UninstallerMissing(InstalledProgram p)
    {
        if (!p.HasUninstaller) return true;
        if (p.ProductCode is not null) return false;
        var exe = _s.Folders.Expand(CommandLine.Split(p.UninstallString!).Executable);
        return WinPath.IsRooted(exe) && !File.Exists(exe);
    }

    private IReadOnlyList<ProgramItemViewModel> Targets() =>
        SelectedItems.Count > 0 ? SelectedItems : Selected is null ? [] : [Selected];

    private bool HasTargets() => Targets().Count > 0;

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private Task UninstallAsync() => StartUninstall(quick: false);

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private Task QuickUninstallAsync() => StartUninstall(quick: true);

    private async Task StartUninstall(bool quick)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        var names = string.Join("\n", targets.Take(8).Select(t => "  • " + t.Name)) + (targets.Count > 8 ? $"\n  …and {targets.Count - 8} more" : "");
        var question = quick
            ? $"Quick uninstall will run the uninstaller{(targets.Count > 1 ? "s" : "")} and then delete leftovers marked Safe without asking:\n\n{names}\n\nContinue?"
            : $"Uninstall the following?\n\n{names}";
        if (!Dialogs.Confirm(question)) return;

        var jobs = targets.Select(t => new UninstallJob
        {
            Title = t.Name,
            Program = t.Program,
            RunUninstaller = true,
            Icon = t.Icon,
        }).ToList();
        Dialogs.ShowUninstallWizard(new UninstallWizardViewModel(_s, jobs, quick));
        await RefreshAsync();
    }

    public async Task UninstallProgramAsync(InstalledProgram program)
    {
        Selected = Programs.FirstOrDefault(p => p.Program.RegistryKey.Equals(program.RegistryKey));
        var job = new UninstallJob { Title = program.DisplayName, Program = program, RunUninstaller = true, Icon = Selected?.Icon };
        Dialogs.ShowUninstallWizard(new UninstallWizardViewModel(_s, [job], quick: false));
        await RefreshAsync();
    }

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenFolder()
    {
        if (Selected?.InstallLocation is { } dir) Shell.OpenFolder(dir);
        else Dialogs.Info("This program didn't register an install folder.");
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenRegistry()
    {
        if (Selected is not null) Shell.OpenRegistryKey(Selected.Program.RegistryKey);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void SearchOnline()
    {
        if (Selected is not null) Shell.SearchWeb($"{Selected.Name} {Selected.Publisher}".Trim());
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenHelpLink()
    {
        if (Selected?.HelpLink is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            Shell.OpenUrl(url);
    }

    /// <summary>Removes a dead entry from the list without running anything (registry is backed up).</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task RemoveEntryAsync()
    {
        if (Selected is null) return;
        var p = Selected.Program;
        if (!Dialogs.ConfirmWarning(
                $"Remove \"{p.DisplayName}\" from the list?\n\nThis only deletes its uninstall entry from the registry (a backup is kept). " +
                "Use it for programs that are already gone. The program's files are not touched."))
            return;
        var item = Scour.Core.Leftovers.LeftoverItem.ForKey(p.RegistryKey, Scour.Core.Leftovers.Confidence.Safe, "Uninstall entry removed by user");
        var report = _s.CreateRemover().Remove(p.DisplayName, [item], new RemovalOptions { BackupRegistry = true }, DateTime.UtcNow);
        if (report.Failed.Count > 0) Dialogs.Error("Could not remove the entry: " + report.Failed[0].Error);
        else ChangeHistory.Record("Programs", $"Removed {p.DisplayName} from the program list", null, ChangeHistory.ForBackup(report.Backup));
        await RefreshAsync();
    }

    [RelayCommand]
    private void ExportList()
    {
        var path = Dialogs.SaveFile("Export program list", "CSV file (*.csv)|*.csv", $"Installed programs {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.csv");
        if (path is null) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Name,Version,Publisher,Install date,Size (MB),Type,Architecture,Scope,Install location,Uninstall string\r\n");
        foreach (var p in View.Cast<ProgramItemViewModel>())
            sb.Append(string.Join(",", Q(p.Name), Q(p.Version), Q(p.Publisher), Q(p.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                                  p.SizeBytes > 0 ? (p.SizeBytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "",
                                  Q(p.Type), Q(p.Architecture), Q(p.Scope), Q(p.InstallLocation), Q(p.Program.UninstallString))).Append("\r\n");
        try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception e) { Dialogs.Error("Could not save the file: " + e.Message); }
    }
}
