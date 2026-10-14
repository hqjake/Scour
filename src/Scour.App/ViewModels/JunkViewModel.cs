using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Junk;

namespace Scour.App.ViewModels;

public sealed partial class JunkCategoryViewModel(string name, string description, bool isChecked, JunkCategory? category) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public JunkCategory? Category { get; } = category;

    [ObservableProperty] private bool _isChecked = isChecked;
    [ObservableProperty] private string _sizeText = "";

    public JunkScanResult? Result { get; set; }
    public long Bytes { get; set; }
}

public sealed partial class JunkViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly JunkCategoryViewModel _recycleBin;

    public JunkViewModel(AppServices services)
    {
        _s = services;
        foreach (var c in services.Junk.Categories)
            Categories.Add(new JunkCategoryViewModel(c.Name, c.Description, c.EnabledByDefault, c));
        _recycleBin = new JunkCategoryViewModel("Recycle Bin", "Files you deleted earlier, including leftovers Scour moved there.", false, null);
        Categories.Add(_recycleBin);
        StatusText = "Choose what to look for, then scan.";
    }

    public override string Title => "Junk cleaner";
    public override string Glyph => "";

    public ObservableCollection<JunkCategoryViewModel> Categories { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool _hasScanned;

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
                    c.SizeText = $"{Format.BytesOrZero(c.Bytes)} · {Format.Plural(c.Result.Files.Count, "file")}";
                }
                else
                {
                    var (bytes, count) = RecycleBin.Query();
                    c.Bytes = bytes;
                    c.SizeText = $"{Format.BytesOrZero(bytes)} · {Format.Plural((int)count, "item")}";
                }
                total += c.Bytes;
            }
            StatusText = $"Found {Format.BytesOrZero(total)} of junk.";
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
        var chosen = Categories.Where(c => c.IsChecked && (c.Result is not null || c == _recycleBin)).ToList();
        if (chosen.Count == 0) return;
        if (!Dialogs.Confirm($"Permanently delete the junk found in {Format.Plural(chosen.Count, "category", "categories")}?\n\nFiles in use are skipped.")) return;
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
            StatusText = $"Freed {Format.BytesOrZero(freed)}." + (skipped > 0 ? $" {Format.Plural(skipped, "file")} in use were skipped." : "");
            HasScanned = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
