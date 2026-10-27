using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Junk;

namespace Scour.App.ViewModels;

public sealed partial class JunkCategoryViewModel(string name, string description, string group, bool isChecked, JunkCategory? category) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Group { get; } = group;
    public JunkCategory? Category { get; } = category;

    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private string _sizeText = "";

    public JunkScanResult? Result { get; set; }
    public long Bytes { get; set; }
}

public sealed partial class SystemActionViewModel(SystemCleanupAction action) : ObservableObject
{
    public SystemCleanupAction Action { get; } = action;
    public string Name => Action.Name;
    public string Description => Action.Description;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _sizeText = "";
}

public sealed partial class JunkViewModel : PageViewModel
{
    private static readonly Dictionary<string, string[]> CategoryProcesses = new()
    {
        ["chrome"] = ["chrome"], ["edge"] = ["msedge"], ["firefox"] = ["firefox"], ["brave"] = ["brave"], ["opera"] = ["opera"],
        ["vivaldi"] = ["vivaldi"], ["discord"] = ["Discord"], ["spotify"] = ["Spotify"], ["teams"] = ["Teams", "ms-teams"],
        ["slack"] = ["slack"], ["vscode"] = ["Code"],
    };

    private readonly AppServices _s;
    private readonly JunkCategoryViewModel _recycleBin;

    public JunkViewModel(AppServices services)
    {
        _s = services;
        foreach (var c in services.Junk.Categories)
            Categories.Add(new JunkCategoryViewModel(c.Name, c.Description, c.Group, c.EnabledByDefault, c));
        _recycleBin = new JunkCategoryViewModel("Recycle Bin", "Files you deleted earlier, including leftovers Scour moved there.", "Windows", false, null);
        Categories.Insert(0, _recycleBin);
        CategoriesView = CollectionViewSource.GetDefaultView(Categories);
        CategoriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(JunkCategoryViewModel.Group)));
        foreach (var a in SystemCleanup.Create(services.FileSystem)) SystemActions.Add(new SystemActionViewModel(a));
        StatusText = "Choose what to look for, then scan.";
    }

    public override string Title => "Junk cleaner";
    public override string Glyph => "\uE894";
    public override string Group => "Cleaning";

    public ObservableCollection<JunkCategoryViewModel> Categories { get; } = [];
    public ICollectionView CategoriesView { get; }
    public ObservableCollection<SystemActionViewModel> SystemActions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _hasScanned;

    [RelayCommand]
    private void SelectAll(string? group)
    {
        foreach (var c in Categories.Where(c => group is null || c.Group == group)) c.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var c in Categories) c.IsChecked = false;
        foreach (var a in SystemActions) a.IsChecked = false;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsLoading = true;
        try
        {
            long total = 0;
            foreach (var c in Categories)
            {
                c.SizeText = "";
                c.Result = null;
                c.Bytes = 0;
                if (!c.IsChecked) continue;
                StatusText = $"Scanning {c.Name}…";
                if (c.Category is { } cat)
                {
                    c.Result = await Task.Run(() => _s.Junk.Scan(cat, DateTime.UtcNow));
                    c.Bytes = c.Result.TotalBytes;
                    c.SizeText = c.Result.Files.Count == 0 ? "Clean" : $"{Format.BytesOrZero(c.Bytes)} · {Format.Plural(c.Result.Files.Count, "file")}";
                }
                else
                {
                    var (bytes, count) = await Task.Run(RecycleBin.Query);
                    c.Bytes = bytes;
                    c.SizeText = count == 0 ? "Empty" : $"{Format.BytesOrZero(bytes)} · {Format.Plural((int)count, "item")}";
                }
                total += c.Bytes;
            }
            foreach (var a in SystemActions.Where(a => a.IsChecked))
            {
                StatusText = $"Checking {a.Name}…";
                try { a.SizeText = await a.Action.Estimate(CancellationToken.None); }
                catch (Exception e) { a.SizeText = e.Message; }
            }
            StatusText = $"Found {Format.BytesOrZero(total)} of junk.";
            SoundService.Play(UiSound.Scan);
            HasScanned = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasScanned))]
    private async Task CleanAsync()
    {
        var chosen = Categories.Where(c => c.IsChecked && (c.Result is { Files.Count: > 0 } || c == _recycleBin && c.Bytes > 0)).ToList();
        var actions = SystemActions.Where(a => a.IsChecked).ToList();
        if (chosen.Count == 0 && actions.Count == 0)
        {
            StatusText = "Nothing to clean.";
            return;
        }

        var running = SystemCleanup.Running(chosen.Where(c => c.Category is not null && CategoryProcesses.ContainsKey(c.Category.Id))
                                                  .SelectMany(c => CategoryProcesses[c.Category!.Id]));
        var warn = running.Count > 0 ? $"\n\nStill running: {string.Join(", ", running)}. Their files in use will be skipped — close them for a full clean." : "";
        if (!Dialogs.Confirm($"Permanently delete the selected junk?{warn}")) return;

        IsLoading = true;
        try
        {
            long freed = 0;
            int skipped = 0;
            foreach (var c in chosen)
            {
                StatusText = $"Cleaning {c.Name}…";
                if (c.Result is { } r)
                {
                    var res = await Task.Run(() => _s.Junk.Clean(r));
                    freed += res.BytesFreed;
                    skipped += res.FilesSkipped;
                }
                else if (c == _recycleBin)
                {
                    var bytes = c.Bytes;
                    if (await Task.Run(RecycleBin.Empty)) freed += bytes;
                }
                c.SizeText = "Cleaned";
                c.Result = null;
            }
            var results = new List<string>();
            foreach (var a in actions)
            {
                var progress = new Progress<string>(s => StatusText = s);
                try { results.Add(await a.Action.Run(progress, CancellationToken.None)); }
                catch (Exception e) { results.Add($"{a.Name}: {e.Message}"); }
                a.SizeText = "Done";
            }
            StatusText = $"Freed {Format.BytesOrZero(freed)}." + (skipped > 0 ? $" {Format.Plural(skipped, "file")} in use were skipped." : "") +
                         (results.Count > 0 ? " " + string.Join(" ", results) : "");
            HasScanned = false;
            ChangeHistory.Record("Cleaning", $"Cleaned junk files: freed {Format.BytesOrZero(freed)}", StatusText);
            _s.Settings.LastCleanupUtc = DateTime.UtcNow;
            _s.Settings.Save();
            SoundService.Play(UiSound.Success);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
