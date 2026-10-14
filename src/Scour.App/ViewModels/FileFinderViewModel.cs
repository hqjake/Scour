using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Junk;

namespace Scour.App.ViewModels;

public sealed partial class FoundFileViewModel(FoundFile file, string? group = null) : ObservableObject
{
    public FoundFile File { get; } = file;
    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path);
    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";
    public string SizeText => Format.Bytes(File.Size);
    public long Size => File.Size;
    public DateTime Modified => File.ModifiedUtc.ToLocalTime();
    public string? Group { get; } = group;

    [ObservableProperty] private bool _isChecked;
}

public enum FinderMode
{
    Duplicates,
    LargeFiles,
}

/// <summary>Duplicate finder and large/old file finder.</summary>
public sealed partial class FileFinderViewModel(AppServices services) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "Duplicates & large files";
    public override string Glyph => "\uE8C8";
    public override string Group => "Cleaning";

    public ObservableCollection<string> Folders { get; } = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
    public ObservableCollection<FoundFileViewModel> Results { get; } = [];
    public System.ComponentModel.ICollectionView ResultsView => _resultsView ??= CreateView();
    private System.ComponentModel.ICollectionView? _resultsView;

    [ObservableProperty] private FinderMode _mode = FinderMode.Duplicates;
    [ObservableProperty] private double _minSizeMb = 1;
    [ObservableProperty] private int _olderThanMonths;
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private string _selectionText = "";

    private System.ComponentModel.ICollectionView CreateView()
    {
        var v = System.Windows.Data.CollectionViewSource.GetDefaultView(Results);
        return v;
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dir = Dialogs.PickFolder("Add a folder to search");
        if (dir is not null && !Folders.Contains(dir, StringComparer.OrdinalIgnoreCase)) Folders.Add(dir);
    }

    [RelayCommand]
    private void AddDrives()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            if (!Folders.Contains(d.RootDirectory.FullName, StringComparer.OrdinalIgnoreCase)) Folders.Add(d.RootDirectory.FullName);
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder is not null) Folders.Remove(SelectedFolder);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (Folders.Count == 0) return;
        _cts = new CancellationTokenSource();
        IsLoading = true;
        Results.Clear();
        ResultsView.GroupDescriptions.Clear();
        var options = new FinderOptions { MinSize = (long)(Math.Max(0, MinSizeMb) * 1024 * 1024) };
        var finder = new FileFinders(services.FileSystem);
        var progress = new Progress<string>(s => StatusText = s + "…");
        try
        {
            if (Mode == FinderMode.Duplicates)
            {
                var groups = await Task.Run(() => finder.FindDuplicates(Folders.ToList(), options, progress, _cts.Token));
                int n = 0;
                foreach (var g in groups)
                {
                    var label = $"{Format.Bytes(g.Size)} × {g.Files.Count} copies  ·  {Format.Bytes(g.Wasted)} wasted  ·  #{++n}";
                    // Keep the oldest copy by default; tick the rest.
                    for (int i = 0; i < g.Files.Count; i++) Results.Add(Track(new FoundFileViewModel(g.Files[i], label) { IsChecked = i > 0 }));
                }
                ResultsView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FoundFileViewModel.Group)));
                StatusText = groups.Count == 0 ? "No duplicates found." :
                    $"{Format.Plural(groups.Count, "set")} of duplicates · {Format.Bytes(groups.Sum(g => g.Wasted))} could be freed. The oldest copy in each set is kept.";
            }
            else
            {
                DateTime? cutoff = OlderThanMonths > 0 ? DateTime.UtcNow.AddMonths(-OlderThanMonths) : null;
                var files = await Task.Run(() => finder.FindLargeFiles(Folders.ToList(), options, cutoff, 1000, _cts.Token));
                foreach (var f in files) Results.Add(Track(new FoundFileViewModel(f)));
                StatusText = $"{Format.Plural(files.Count, "file")} · {Format.Bytes(files.Sum(f => f.Size))}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        finally
        {
            IsLoading = false;
            UpdateSelection();
        }
    }

    private FoundFileViewModel Track(FoundFileViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FoundFileViewModel.IsChecked)) UpdateSelection();
        };
        return vm;
    }

    private void UpdateSelection()
    {
        var sel = Results.Where(r => r.IsChecked).ToList();
        SelectionText = sel.Count == 0 ? "" : $"{Format.Plural(sel.Count, "file")} selected · {Format.Bytes(sel.Sum(s => s.Size))}";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLocation(FoundFileViewModel? f)
    {
        if (f is not null) Shell.OpenFolder(f.Path);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var sel = Results.Where(r => r.IsChecked).ToList();
        if (sel.Count == 0) return;
        if (Mode == FinderMode.Duplicates)
        {
            // Never delete every copy of a file.
            var wipesGroup = sel.GroupBy(s => s.Group).Any(g => Results.Count(r => r.Group == g.Key) == g.Count());
            if (wipesGroup)
            {
                Dialogs.Error("At least one set has every copy selected. Untick one copy in each set to keep it.");
                return;
            }
        }
        var where = services.Settings.FilesToRecycleBin ? "to the Recycle Bin" : "permanently";
        if (!Dialogs.Confirm($"Delete {Format.Plural(sel.Count, "file")} ({Format.Bytes(sel.Sum(s => s.Size))}) {where}?")) return;

        IsLoading = true;
        int failed = 0;
        await Task.Run(() =>
        {
            foreach (var f in sel)
            {
                try { services.FileSystem.DeleteFile(f.Path, services.Settings.FilesToRecycleBin); }
                catch { failed++; }
            }
        });
        foreach (var f in sel.Where(f => !File.Exists(f.Path))) Results.Remove(f);
        IsLoading = false;
        UpdateSelection();
        StatusText = failed > 0 ? $"{failed} file(s) could not be deleted (in use or access denied)." : "Deleted.";
    }
}
