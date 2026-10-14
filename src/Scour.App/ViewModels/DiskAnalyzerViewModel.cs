using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Controls;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.DiskUsage;

namespace Scour.App.ViewModels;

/// <summary>Lazily-expanded tree row for a <see cref="DiskNode"/>.</summary>
public sealed partial class DiskNodeViewModel : ObservableObject
{
    private static readonly DiskNodeViewModel Placeholder = new();
    private const int MaxChildren = 300;

    private DiskNodeViewModel() => Node = new DiskNode("", false);

    public DiskNodeViewModel(DiskNode node, DiskNodeViewModel? parent)
    {
        Node = node;
        Parent = parent;
        if (node.IsDirectory && node.Children is { Count: > 0 }) Children.Add(Placeholder);
    }

    public DiskNode Node { get; }
    public DiskNodeViewModel? Parent { get; }
    public ObservableCollection<DiskNodeViewModel> Children { get; } = [];

    public string Name => Node.Name;
    public string SizeText => Format.Bytes(Node.Size);
    public double Percent => Node.Parent is { Size: > 0 } p ? 100.0 * Node.Size / p.Size : 100;
    public string PercentText => $"{Percent:0.0}%";
    public string Details => Node.IsDirectory ? $"{Node.FileCount:N0} files" : Node.ModifiedUtc == default ? "" : Node.ModifiedUtc.ToLocalTime().ToString("d");
    public string Glyph => Node.IsDirectory ? "\uE8B7" : "\uE8A5";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || Children.Count != 1 || !ReferenceEquals(Children[0], Placeholder)) return;
        Children.Clear();
        foreach (var c in Node.Children!.Take(MaxChildren)) Children.Add(new DiskNodeViewModel(c, this));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(Details));
    }
}

public sealed record DriveChoice(string Root, string Label, bool IsNtfs);

public sealed record ExtensionRow(string Extension, string SizeText, int Count, double Percent, Brush Swatch);

public sealed partial class DiskAnalyzerViewModel(AppServices services) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "Disk space analyzer";
    public override string Glyph => "\uEDA2";
    public override string Group => "Disks";

    public ObservableCollection<DriveChoice> Drives { get; } = [];
    public ObservableCollection<DiskNodeViewModel> Tree { get; } = [];
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];
    public ObservableCollection<DiskNode> LargestFiles { get; } = [];

    [ObservableProperty] private DriveChoice? _selectedDrive;
    [ObservableProperty] private DiskNode? _root;
    [ObservableProperty] private DiskNode? _focus;
    [ObservableProperty] private int _version;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(OpenCommand), nameof(ZoomCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedPath), nameof(SelectedSize))]
    private DiskNode? _selected;

    public string SelectedPath => Selected?.FullPath ?? "";
    public string SelectedSize => Selected is null ? "" : Format.Bytes(Selected.Size) + (Selected.IsDirectory ? $" · {Selected.FileCount:N0} files · {Selected.FolderCount:N0} folders" : "");

    protected override Task LoadAsync()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            bool ntfs = string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local disk" : d.VolumeLabel;
            Drives.Add(new DriveChoice(d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')}  {label}  —  {Format.Bytes(d.TotalSize - d.TotalFreeSpace)} used of {Format.Bytes(d.TotalSize)}" + (ntfs ? "" : $"  ({d.DriveFormat})"), ntfs));
        }
        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                        ?? Drives.FirstOrDefault();
        StatusText = "Pick a drive and press Scan. NTFS drives are read straight from the Master File Table — usually a few seconds for a whole disk.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ScanAsync() => SelectedDrive is null ? Task.CompletedTask : RunScan(SelectedDrive.Root, SelectedDrive.IsNtfs);

    [RelayCommand]
    private Task ScanFolderAsync()
    {
        var dir = Dialogs.PickFolder("Choose a folder to analyze");
        return dir is null ? Task.CompletedTask : RunScan(dir, useMft: false);
    }

    private async Task RunScan(string path, bool useMft)
    {
        _cts = new CancellationTokenSource();
        IsLoading = true;
        Progress = 0;
        var sw = Stopwatch.StartNew();
        string method = "folder scan";
        try
        {
            var ct = _cts.Token;
            DiskNode root;
            if (useMft)
            {
                try
                {
                    StatusText = "Reading the Master File Table…";
                    var progress = new Progress<double>(p => Progress = p * 100);
                    root = await Task.Run(() =>
                    {
                        using var volume = VolumeAccess.OpenVolume(path);
                        return new MftScanner().Scan(volume, path.TrimEnd('\\') + "\\", progress, ct);
                    }, ct);
                    method = "MFT scan";
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    StatusText = $"Direct MFT read unavailable ({e.Message}); scanning folders instead…";
                    root = await FolderScan(path, ct);
                }
            }
            else
            {
                root = await FolderScan(path, ct);
            }
            sw.Stop();
            SetRoot(root);
            StatusText = $"{method}: {root.FileCount:N0} files in {root.FolderCount:N0} folders, {Format.Bytes(root.Size)}, in {sw.Elapsed.TotalSeconds:0.0} s.";
            SoundService.Play(UiSound.Scan);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        finally
        {
            IsLoading = false;
            Progress = 0;
        }
    }

    private Task<DiskNode> FolderScan(string path, CancellationToken ct)
    {
        var progress = new Progress<string>(p => StatusText = "Scanning " + p);
        return Task.Run(() => new FolderScanner(services.FileSystem).Scan(path, progress, ct), ct);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void SetRoot(DiskNode root)
    {
        Root = root;
        Focus = root;
        Tree.Clear();
        var rootVm = new DiskNodeViewModel(root, null) { IsExpanded = true };
        Tree.Add(rootVm);
        RefreshStats();
    }

    private void RefreshStats()
    {
        if (Focus is null) return;
        Extensions.Clear();
        var stats = DiskStats.ByExtension(Focus);
        foreach (var s in stats.Take(60))
        {
            var brush = new SolidColorBrush(TreemapControl.ColorFor(s.Extension));
            brush.Freeze();
            Extensions.Add(new ExtensionRow(s.Extension, Format.Bytes(s.Size), s.Count, Focus.Size == 0 ? 0 : 100.0 * s.Size / Focus.Size, brush));
        }
        LargestFiles.Clear();
        foreach (var f in DiskStats.LargestFiles(Focus, 1000)) LargestFiles.Add(f);
        Summary = $"{Focus.FullPath}  ·  {Format.Bytes(Focus.Size)}  ·  {Focus.FileCount:N0} files";
        Version++;
    }

    public void ZoomTo(DiskNode node)
    {
        if (!node.IsDirectory) return;
        Focus = node;
        RefreshStats();
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Zoom()
    {
        if (Selected is null) return;
        ZoomTo(Selected.IsDirectory ? Selected : Selected.Parent ?? Selected);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (Focus?.Parent is { } p) ZoomTo(p);
    }

    [RelayCommand]
    private void ZoomReset()
    {
        if (Root is not null) ZoomTo(Root);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Open()
    {
        if (Selected is not null) Shell.OpenFolder(Selected.FullPath);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { Parent: not null } node) return;
        var guard = new Scour.Core.Leftovers.SafetyGuard(services.Folders, [], []);
        if (guard.IsProtectedPath(node.FullPath))
        {
            Dialogs.Error("That's a Windows or shared system folder and can't be deleted here.");
            return;
        }
        var where = services.Settings.FilesToRecycleBin ? "to the Recycle Bin" : "permanently";
        if (!Dialogs.ConfirmWarning($"Delete {node.FullPath} ({Format.Bytes(node.Size)}) {where}?")) return;
        IsLoading = true;
        try
        {
            await Task.Run(() =>
            {
                if (node.IsDirectory) services.FileSystem.DeleteDirectory(node.FullPath, services.Settings.FilesToRecycleBin);
                else services.FileSystem.DeleteFile(node.FullPath, services.Settings.FilesToRecycleBin);
            });
            var freed = node.Size;
            node.Detach();
            Selected = null;
            if (Focus is not null && IsUnder(Focus, node)) Focus = Root;
            Tree.Clear();
            if (Root is not null) Tree.Add(new DiskNodeViewModel(Root, null) { IsExpanded = true });
            RefreshStats();
            StatusText = $"Deleted {Format.Bytes(freed)}.";
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e)
        {
            Dialogs.Error("Could not delete: " + e.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool IsUnder(DiskNode node, DiskNode ancestor)
    {
        for (var n = node; n is not null; n = n.Parent) if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }
}
