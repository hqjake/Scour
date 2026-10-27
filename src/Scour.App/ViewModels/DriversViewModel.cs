using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Drivers;

namespace Scour.App.ViewModels;

public sealed partial class DriverItemViewModel(DriverPackage package, DriverState state) : ObservableObject
{
    public DriverPackage Package { get; } = package;
    public DriverState State { get; } = state;
    public string Name => string.IsNullOrEmpty(Package.Info.Provider) ? Package.OriginalName : $"{Package.Info.Provider} — {Package.OriginalName}";
    public string Class => Package.Info.Class;
    public string Version => Package.Info.Version?.ToString() ?? "";
    public string Date => Package.Info.Date?.ToString("d") ?? "";
    public string Size => Format.Bytes(Package.SizeBytes);
    public string StateText => State switch
    {
        DriverState.InUse => "In use",
        DriverState.Old => "Old version",
        _ => "Not in use (newest copy)",
    };
    public string UsedBy => Package.Devices.Count == 0 ? "" : string.Join(", ", Package.Devices.Take(3)) + (Package.Devices.Count > 3 ? $" +{Package.Devices.Count - 3}" : "");
    public bool CanRemove => State == DriverState.Old;

    [ObservableProperty] private bool _isChecked = state == DriverState.Old;
    [ObservableProperty] private string _result = "";
}

/// <summary>Old driver versions in the driver store, plus driver backup and restore for a reset or new PC.</summary>
public sealed partial class DriversViewModel : PageViewModel
{
    public override string Title => "Drivers";
    public override string Glyph => "\uE950";
    public override string Group => "System";

    public ObservableCollection<DriverItemViewModel> Drivers { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _oldText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasOld;

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Reading the driver store and asking Windows which drivers its devices use…";
        try
        {
            var packages = await Task.Run(() => DriverService.List());
            var states = DriverStore.Classify(packages);
            Drivers.Clear();
            foreach (var p in packages.OrderBy(p => states[p.PublishedName]).ThenByDescending(p => p.SizeBytes))
                Drivers.Add(new DriverItemViewModel(p, states[p.PublishedName]));
            var old = Drivers.Where(d => d.CanRemove).ToList();
            HasOld = old.Count > 0;
            TotalText = $"{Format.Plural(packages.Count, "driver package")} · {Format.Bytes(packages.Sum(p => p.SizeBytes))}";
            OldText = old.Count == 0 ? "No old versions" : $"{Format.Plural(old.Count, "old version")} · {Format.Bytes(old.Sum(d => d.Package.SizeBytes))} to free";
            StatusText = old.Count == 0
                ? "Your driver store is tidy. Back up your drivers before resetting Windows so you don't have to hunt for them again."
                : "Old versions are copies Windows keeps after a driver update. The newest copy of every driver is always kept.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            StatusText = "Couldn't read the drivers: " + e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveOldAsync()
    {
        var chosen = Drivers.Where(d => d.IsChecked && d.CanRemove).ToList();
        if (chosen.Count == 0) return;
        var size = Format.Bytes(chosen.Sum(d => d.Package.SizeBytes));
        if (!Dialogs.Confirm($"Remove {Format.Plural(chosen.Count, "old driver version")} ({size})?\n\nWindows refuses to remove any driver a device is using, so this can't break working hardware.")) return;
        IsBusy = IsLoading = true;
        Log.Clear();
        int ok = 0;
        try
        {
            foreach (var d in chosen)
            {
                StatusText = $"Removing {d.Package.PublishedName} ({d.Name})…";
                var (code, output) = await DriverService.PnpUtilAsync(DriverStore.DeleteArguments(d.Package.PublishedName), TimeSpan.FromMinutes(2));
                Log.Add($"── {d.Package.PublishedName}: exit {code}");
                foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) Log.Add("   " + line);
                d.Result = code is 0 or 3010 ? "Removed" : "Windows kept it";
                if (code is 0 or 3010) ok++;
            }
            SoundService.Play(ok == chosen.Count ? UiSound.Success : UiSound.Warning);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorLog.Write("Removing old drivers", e);
            Log.Add("Stopped: " + e.Message);
        }
        finally
        {
            IsBusy = IsLoading = false;
        }
        await RefreshAsync();
        StatusText = $"Removed {Format.Plural(ok, "old driver version")}" + (ok < chosen.Count ? $"; Windows kept {chosen.Count - ok} it still needs." : ".");
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        var root = Dialogs.PickFolder("Choose where to save the driver backup (a USB stick or another drive is best)");
        if (root is null) return;
        var folder = Path.Combine(root, $"Drivers - {Environment.MachineName} - {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        IsBusy = IsLoading = true;
        StatusText = "Exporting every third-party driver… this can take a few minutes.";
        try
        {
            Directory.CreateDirectory(folder);
            var (code, output) = await DriverService.PnpUtilAsync(DriverStore.ExportArguments(folder), TimeSpan.FromMinutes(20));
            int count = Directory.Exists(folder) ? Directory.GetDirectories(folder).Length : 0;
            if (code == 0 || count > 0)
            {
                StatusText = $"Saved {Format.Plural(count, "driver")} to {folder}. After reinstalling Windows, use Restore drivers here.";
                SoundService.Play(UiSound.Success);
            }
            else
            {
                StatusText = "The backup failed: " + output.Trim().Split('\n').LastOrDefault();
                SoundService.Play(UiSound.Warning);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorLog.Write("Backing up drivers", e);
            StatusText = "The backup failed: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
        finally
        {
            IsBusy = IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var folder = Dialogs.PickFolder("Choose a driver backup folder");
        if (folder is null) return;
        if (!Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any())
        {
            Dialogs.Error("That folder doesn't contain any drivers (.inf files).");
            return;
        }
        if (!Dialogs.Confirm("Install every driver in this folder? Windows uses each one for matching hardware; drivers for devices you don't have are just stored.")) return;
        IsBusy = IsLoading = true;
        StatusText = "Installing drivers…";
        string message;
        try
        {
            var (code, _) = await DriverService.PnpUtilAsync(DriverStore.RestoreArguments(folder), TimeSpan.FromMinutes(30));
            message = code is 0 or 3010
                ? "Drivers installed." + (code == 3010 ? " Restart the PC to finish." : "")
                : $"Finished with some errors (code {code}) — usually drivers that don't fit this PC's hardware.";
            SoundService.Play(code is 0 or 3010 ? UiSound.Success : UiSound.Warning);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ErrorLog.Write("Restoring drivers", e);
            message = "Restoring drivers failed: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
        finally
        {
            IsBusy = IsLoading = false;
        }
        await RefreshAsync();
        StatusText = message;
    }
}
