using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Registry;

namespace Scour.App.ViewModels;

public sealed partial class RegistryCategoryViewModel(RegistryCategoryInfo info) : ObservableObject
{
    public RegistryCategoryInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Description => Info.Description;

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private string _countText = "";
}

public sealed partial class RegistryIssueViewModel(RegistryIssue issue, string categoryName) : ObservableObject
{
    public RegistryIssue Issue { get; } = issue;
    public string CategoryName { get; } = categoryName;
    public string Problem => Issue.Problem;
    public string Location => Issue.Item.DisplayText;

    [ObservableProperty] private bool _isChecked = true;
}

public sealed partial class RegistryCleanerViewModel : PageViewModel
{
    private readonly AppServices _s;

    public RegistryCleanerViewModel(AppServices services)
    {
        _s = services;
        foreach (var c in RegistryCleaner.Categories) Categories.Add(new RegistryCategoryViewModel(c));
        IssuesView = CollectionViewSource.GetDefaultView(Issues);
        IssuesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RegistryIssueViewModel.CategoryName)));
        StatusText = "Every fix is backed up first and can be undone from the Backups page.";
    }

    public override string Title => "Registry cleaner";
    public override string Glyph => "";
    public override string Group => "Cleaning";

    public ObservableCollection<RegistryCategoryViewModel> Categories { get; } = [];
    public ObservableCollection<RegistryIssueViewModel> Issues { get; } = [];
    public ICollectionView IssuesView { get; }

    [ObservableProperty] private string _selectionText = "";

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsLoading = true;
        Issues.Clear();
        try
        {
            var selected = Categories.Where(c => c.IsChecked).Select(c => c.Info.Category).ToHashSet();
            var progress = new Progress<string>(s => StatusText = $"Checking {s.ToLowerInvariant()}…");
            var cleaner = new RegistryCleaner(_s.Registry, _s.FileSystem, _s.Folders);
            var issues = await Task.Run(() => cleaner.Scan(selected, progress));
            foreach (var c in Categories)
            {
                int n = issues.Count(i => i.Category == c.Info.Category);
                c.CountText = !c.IsChecked ? "" : n == 0 ? "No problems" : Format.Plural(n, "problem");
            }
            foreach (var i in issues)
            {
                var vm = new RegistryIssueViewModel(i, RegistryCleaner.Categories.First(c => c.Category == i.Category).Name);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RegistryIssueViewModel.IsChecked)) UpdateSelection(); };
                Issues.Add(vm);
            }
            StatusText = issues.Count == 0 ? "No problems found." : $"Found {Format.Plural(issues.Count, "problem")}.";
        }
        finally
        {
            IsLoading = false;
            UpdateSelection();
            FixCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateSelection()
    {
        int n = Issues.Count(i => i.IsChecked);
        SelectionText = Issues.Count == 0 ? "" : $"{n} of {Issues.Count} selected";
        FixCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Issues) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Issues) i.IsChecked = false;
    }

    private bool CanFix() => Issues.Any(i => i.IsChecked);

    [RelayCommand(CanExecute = nameof(CanFix))]
    private async Task FixAsync()
    {
        var items = Issues.Where(i => i.IsChecked).Select(i => i.Issue.Item).ToList();
        if (!Dialogs.Confirm($"Fix {Format.Plural(items.Count, "problem")}?\n\nA backup is saved first; you can restore it from the Backups page.")) return;
        IsLoading = true;
        try
        {
            var options = new Scour.Core.Removal.RemovalOptions { BackupRegistry = true, FilesToRecycleBin = true };
            var report = await Task.Run(() => _s.CreateRemover().Remove("Registry cleaner", items, options, DateTime.UtcNow));
            foreach (var removed in report.Removed)
            {
                var vm = Issues.FirstOrDefault(i => ReferenceEquals(i.Issue.Item, removed));
                if (vm is not null) Issues.Remove(vm);
            }
            StatusText = $"Fixed {Format.Plural(report.Removed.Count, "problem")}." +
                         (report.Failed.Count > 0 ? $" {report.Failed.Count} could not be fixed (access denied)." : "") +
                         " Backup saved.";
            _s.NotifyProgramsChanged();
        }
        finally
        {
            IsLoading = false;
            UpdateSelection();
        }
    }

    [RelayCommand]
    private void Open(RegistryIssueViewModel? issue)
    {
        if (issue?.Issue.Item is not { } item) return;
        if (item.RegistryKey is { } k) Shell.OpenRegistryKey(k);
        else Shell.OpenFolder(item.Path);
    }
}
