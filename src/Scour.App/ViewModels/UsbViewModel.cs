using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Usb;

namespace Scour.App.ViewModels;

public enum UsbTask
{
    Format,
    Bootable,
}

public sealed record Choice<T>(T Value, string Label);

public sealed partial class UsbViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "USB tools";
    public override string Glyph => "";
    public override string Group => "Disks";

    public ObservableCollection<UsbDisk> Disks { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    public IReadOnlyList<string> FileSystems { get; } = ["FAT32", "exFAT", "NTFS"];

    public IReadOnlyList<Choice<int?>> ClusterSizes { get; } =
    [
        new(null, "Default"), new(4096, "4 KB"), new(8192, "8 KB"), new(16384, "16 KB"), new(32768, "32 KB"), new(65536, "64 KB"),
    ];

    public IReadOnlyList<Choice<PartitionScheme>> Schemes { get; } =
    [
        new(PartitionScheme.Mbr, "MBR — BIOS and UEFI PCs"),
        new(PartitionScheme.Gpt, "GPT — UEFI PCs only"),
    ];

    public IReadOnlyList<Choice<BootMode?>> BootModes { get; } =
    [
        new(null, "Automatic (recommended)"),
        new(BootMode.WindowsSetup, "Windows setup"),
        new(BootMode.IsoCopy, "Copy ISO files (UEFI)"),
        new(BootMode.RawImage, "Raw image (DD)"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private UsbDisk? _selectedDisk;

    [ObservableProperty] private UsbTask _mode = UsbTask.Format;
    [ObservableProperty] private string _fileSystem = "FAT32";
    [ObservableProperty] private Choice<int?>? _clusterSize;
    [ObservableProperty] private Choice<PartitionScheme>? _scheme;
    [ObservableProperty] private Choice<BootMode?>? _selectedBootMode;
    [ObservableProperty] private string _label = "USB";
    [ObservableProperty] private bool _quickFormat = true;
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private string _imageInfo = "";
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isWorking;

    partial void OnIsWorkingChanged(bool value) => IsLoading = value;

    protected override async Task LoadAsync()
    {
        ClusterSize = ClusterSizes[0];
        Scheme = Schemes[0];
        SelectedBootMode = BootModes[0];
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var number = SelectedDisk?.Number;
            var disks = await UsbService.ListAsync();
            Disks.Clear();
            foreach (var d in disks) Disks.Add(d);
            SelectedDisk = Disks.FirstOrDefault(d => d.Number == number) ?? Disks.FirstOrDefault();
            StatusText = Disks.Count == 0 ? "No USB drives found. Plug one in and press refresh." : "";
        }
        catch (Exception e)
        {
            StatusText = "Could not list drives: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task BrowseImageAsync()
    {
        var file = Dialogs.PickFile("Choose a bootable image", "Disk images (*.iso;*.img;*.bin;*.raw)|*.iso;*.img;*.bin;*.raw|All files (*.*)|*.*");
        if (file is null) return;
        ImagePath = file;
        Mode = UsbTask.Bootable;
        var info = new FileInfo(file);
        bool hybrid = ReadHybrid(file);
        ImageInfo = $"{info.Name} · {Format.Bytes(info.Length)}" +
                    (IsRawImage(file) ? " · raw disk image" : hybrid ? " · hybrid ISO (can be written raw)" : " · ISO");
        var name = Path.GetFileNameWithoutExtension(file);
        Label = Fat32Formatter.NormalizeLabel(name).Trim();
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private static bool IsRawImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".img" or ".bin" or ".raw" or ".dd";

    private static bool ReadHybrid(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var sector = new byte[512];
            return f.Read(sector, 0, 512) == 512 && BootPlanner.IsHybrid(sector);
        }
        catch { return false; }
    }

    private void Write(string line) => Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");

    private bool CanStart() => SelectedDisk is not null && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedDisk is not { } disk) return;
        Log.Clear();
        Progress = 0;
        _cts = new CancellationTokenSource();
        IsWorking = true;
        try
        {
            if (Mode == UsbTask.Format) await FormatAsync(disk, _cts.Token);
            else await MakeBootableAsync(disk, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Write("Cancelled. The drive is probably unusable until you format it again.");
            SoundService.Play(UiSound.Warning);
        }
        catch (Exception e)
        {
            Write("Failed: " + e.Message);
            Dialogs.Error(e.Message);
        }
        finally
        {
            IsWorking = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private bool ConfirmErase(UsbDisk disk, string what) =>
        Dialogs.ConfirmWarning($"{what}\n\nALL DATA on this drive will be erased:\n\n    {disk.Name}\n    {Format.Bytes(disk.Size)}  ·  Disk {disk.Number}" +
                               (disk.Letters.Length > 0 ? $"  ·  {disk.Letters}" : "") + "\n\nContinue?");

    private async Task FormatAsync(UsbDisk disk, CancellationToken ct)
    {
        var scheme = Scheme?.Value ?? PartitionScheme.Mbr;
        bool fat32 = FileSystem == "FAT32";
        bool ownFat32 = fat32 && disk.Size > (32L << 30);
        if (!ConfirmErase(disk, $"Format as {FileSystem}?")) return;

        Write($"Partitioning disk {disk.Number} ({(scheme == PartitionScheme.Gpt ? "GPT" : "MBR")})…");
        var part = await UsbService.PrepareAsync(disk, scheme, fat32);
        Write($"Created partition {part.Letter}: ({Format.Bytes(part.Size)}).");

        if (ownFat32)
        {
            Write("Windows can't create FAT32 over 32 GB — using Scour's FAT32 formatter…");
            if (!QuickFormat) Write("(Full format isn't available for large FAT32 volumes; doing a quick format.)");
            var progress = new Progress<double>(p => Progress = p * 100);
            await System.Threading.Tasks.Task.Run(() => UsbService.WriteFat32(part, Label, progress, ct), ct);
        }
        else
        {
            Write(QuickFormat ? $"Formatting {FileSystem}…" : $"Full format {FileSystem} (checks every sector; this can take a long time)…");
            await UsbService.FormatVolumeAsync(part.Letter, FileSystem, Label, ClusterSize?.Value, !QuickFormat);
        }
        Progress = 100;
        Write($"Done. {part.Letter}: is ready.");
        SoundService.Play(UiSound.Success);
    }

    private async Task MakeBootableAsync(UsbDisk disk, CancellationToken ct)
    {
        if (!File.Exists(ImagePath)) throw new InvalidOperationException("Choose an ISO or disk image first.");
        var scheme = Scheme?.Value ?? PartitionScheme.Mbr;
        bool rawFile = IsRawImage(ImagePath);
        bool hybrid = ReadHybrid(ImagePath);
        long imageBytes = new FileInfo(ImagePath).Length;

        char? iso = null;
        try
        {
            List<(string Path, long Size)> files = [];
            if (!rawFile)
            {
                Write("Opening the ISO…");
                iso = await UsbService.MountIsoAsync(ImagePath);
                files = await System.Threading.Tasks.Task.Run(() => UsbService.ListFiles($"{iso}:\\"), ct);
                Write($"{files.Count:N0} files, {Format.Bytes(files.Sum(f => f.Size))}.");
            }

            var plan = BootPlanner.Plan(rawFile, hybrid, files, scheme, disk.Size, imageBytes, SelectedBootMode?.Value);
            Write("Mode: " + plan.Mode switch
            {
                BootMode.WindowsSetup => "Windows setup",
                BootMode.IsoCopy => "copy ISO files",
                _ => "raw image (DD)",
            });
            foreach (var w in plan.Warnings) Write("Note: " + w);
            if (plan.Error is not null) throw new InvalidOperationException(plan.Error);
            if (!ConfirmErase(disk, $"Write {Path.GetFileName(ImagePath)} to this drive?")) return;

            if (plan.Mode == BootMode.RawImage)
            {
                if (iso is not null)
                {
                    await UsbService.DismountIsoAsync(ImagePath);
                    iso = null;
                }
                Write("Writing the image to the drive…");
                var p = new Progress<double>(v =>
                {
                    Progress = v * 100;
                    StatusText = $"{Format.Bytes((long)(v * imageBytes))} of {Format.Bytes(imageBytes)}";
                });
                await UsbService.WriteRawAsync(disk, ImagePath, p, ct);
            }
            else
            {
                Write("Partitioning and formatting FAT32…");
                var part = await UsbService.PrepareAsync(disk, scheme, fat32: true);
                if (part.Size > (32L << 30))
                    await System.Threading.Tasks.Task.Run(() => UsbService.WriteFat32(part, Label, null, ct), ct);
                else
                    await UsbService.FormatVolumeAsync(part.Letter, "FAT32", Label, null, full: false);
                await System.Threading.Tasks.Task.Delay(1500, ct); // let Windows remount the new file system

                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (plan.SplitWim && plan.WimRelativePath is not null) skip.Add(plan.WimRelativePath);
                Write("Copying files…");
                var progress = new Progress<(long Done, long Total, string File)>(x =>
                {
                    Progress = x.Total == 0 ? 0 : 100.0 * x.Done / x.Total;
                    StatusText = $"{Format.Bytes(x.Done)} of {Format.Bytes(x.Total)} · {x.File}";
                });
                await UsbService.CopyFilesAsync($"{iso}:\\", $"{part.Letter}:\\", files, skip, progress, ct);

                if (plan.SplitWim && plan.WimRelativePath is not null)
                {
                    Write("Splitting install.wim into 3.8 GB parts (this takes a while)…");
                    StatusText = "Splitting install.wim…";
                    await UsbService.SplitWimAsync(iso!.Value, plan.WimRelativePath, part.Letter, ct);
                }
                if (plan.Mode == BootMode.WindowsSetup && scheme == PartitionScheme.Mbr)
                {
                    Write("Adding legacy BIOS boot code…");
                    if (!await UsbService.WriteBiosBootCodeAsync(iso!.Value, part.Letter))
                        Write("Note: couldn't add BIOS boot code; the stick will still boot on UEFI PCs.");
                }
            }
            Progress = 100;
            StatusText = "";
            Write("Done. The USB drive is ready to boot.");
            SoundService.Play(UiSound.Success);
        }
        finally
        {
            if (iso is not null) await UsbService.DismountIsoAsync(ImagePath);
        }
    }
}
