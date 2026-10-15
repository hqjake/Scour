using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Updates;

namespace Scour.App.ViewModels;

public sealed partial class KitItemViewModel(KitApp app, bool installedHere) : ObservableObject
{
    public KitApp App { get; } = app;
    public string Name { get; } = ReinstallKit.FriendlyName(app.Id);
    public string Id => App.Id;
    public string Version => App.Version ?? "Latest";
    public string Source => App.Source;
    public bool InstalledHere { get; } = installedHere;

    [ObservableProperty] private bool _isChecked = !installedHere;
    [ObservableProperty] private string _status = installedHere ? "Already on this PC" : "";
}

/// <summary>Save the apps on this PC to a file and reinstall them all on a new or reset PC.</summary>
public sealed partial class ReinstallKitViewModel(AppServices services) : PageViewModel
{
    private CancellationTokenSource? _cts;
    private string? _thisPcExport;

    public override string Title => "Reinstall kit";
    public override string Glyph => "";
    public override string Group => "Programs";

    public ObservableCollection<KitItemViewModel> Apps { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _showing = "";
    [ObservableProperty] private bool _isRestoreMode;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private double _progress;

    protected override Task LoadAsync() => ScanThisPcAsync();

    /// <summary>Lists what a kit made on this PC would contain.</summary>
    [RelayCommand]
    private async Task ScanThisPcAsync()
    {
        IsLoading = true;
        Progress = 0;
        StatusText = "Asking winget which of your apps it can reinstall…";
        try
        {
            var apps = await ExportThisPcAsync();
            Apps.Clear();
            foreach (var a in apps.OrderBy(a => ReinstallKit.FriendlyName(a.Id), StringComparer.CurrentCultureIgnoreCase))
                Apps.Add(new KitItemViewModel(a, installedHere: false) { IsChecked = true, Status = "Will be saved" });
            IsRestoreMode = false;
            Showing = $"This PC — {Format.Plural(apps.Count, "app")} winget can reinstall";
            StatusText = apps.Count == 0
                ? "winget didn't recognise any of your apps."
                : "Untick anything you don't want, then press Save kit. Apps winget doesn't know (and their settings) aren't included.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<List<KitApp>> ExportThisPcAsync()
    {
        _thisPcExport = Path.Combine(Path.GetTempPath(), $"scour-kit-{Environment.ProcessId}.json");
        var path = _thisPcExport;
        return await Task.Run(() => WingetService.ExportAsync(path, _ => { }, CancellationToken.None));
    }

    [RelayCommand]
    private async Task SaveKitAsync()
    {
        if (IsRestoreMode || Apps.Count == 0)
        {
            await ScanThisPcAsync();
            if (Apps.Count == 0) return;
        }
        var file = Dialogs.SaveFile("Save reinstall kit", "Scour reinstall kit (*.json)|*.json", $"Scour kit - {Environment.MachineName} - {DateTime.Now:yyyy-MM-dd}.json");
        if (file is null || _thisPcExport is null) return;
        try
        {
            // Keep winget's own format so the kit also works with "winget import".
            var chosen = Apps.Where(a => a.IsChecked).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var json = ReinstallKit.Filter(await File.ReadAllTextAsync(_thisPcExport), chosen);
            await File.WriteAllTextAsync(file, json);
            StatusText = $"Saved {Format.Plural(chosen.Count, "app")} to {Path.GetFileName(file)}. On the new PC, open it here with Restore from kit.";
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e)
        {
            Dialogs.Error("Couldn't save the kit: " + e.Message);
        }
    }

    [RelayCommand]
    private async Task OpenKitAsync()
    {
        var file = Dialogs.PickFile("Open reinstall kit", "Reinstall kit (*.json)|*.json|All files|*.*");
        if (file is null) return;
        List<KitApp> kit;
        try
        {
            kit = ReinstallKit.Parse(await File.ReadAllTextAsync(file));
        }
        catch (Exception e)
        {
            Dialogs.Error("That file isn't a reinstall kit: " + e.Message);
            return;
        }

        IsLoading = true;
        Progress = 0;
        StatusText = "Checking which of these apps are already installed…";
        try
        {
            List<KitApp> here;
            try { here = await ExportThisPcAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { here = []; Log.Add("Couldn't list this PC's apps: " + e.Message); }
            var missing = ReinstallKit.Missing(kit, here).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Apps.Clear();
            foreach (var a in kit.OrderBy(a => !missing.Contains(a.Id)).ThenBy(a => ReinstallKit.FriendlyName(a.Id), StringComparer.CurrentCultureIgnoreCase))
                Apps.Add(new KitItemViewModel(a, installedHere: !missing.Contains(a.Id)));
            IsRestoreMode = true;
            Showing = $"{Path.GetFileName(file)} — {Format.Plural(missing.Count, "app")} to install";
            StatusText = missing.Count == 0 ? "Everything in this kit is already installed." : "Press Install selected to set them all up silently.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var a in Apps.Where(a => !a.InstalledHere)) a.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var a in Apps) a.IsChecked = false;
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var chosen = Apps.Where(a => a.IsChecked && !a.InstalledHere).ToList();
        if (!IsRestoreMode || chosen.Count == 0) return;
        if (!Dialogs.Confirm($"Install {Format.Plural(chosen.Count, "app")}?\n\nApps install silently, one after another, with their default options. You agree to each app's licence terms.")) return;

        _cts = new CancellationTokenSource();
        IsInstalling = IsLoading = true;
        Log.Clear();
        int ok = 0, failed = 0;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                var item = chosen[i];
                Progress = 100.0 * i / chosen.Count;
                StatusText = $"Installing {item.Name} ({i + 1} of {chosen.Count})…";
                item.Status = "Installing…";
                Log.Add($"── {item.Name} ({item.Id})");
                int code;
                try
                {
                    code = await Task.Run(() => WingetService.InstallAsync(item.App,
                        line => dispatcher.BeginInvoke(() => { if (!IsSpinner(line)) Log.Add("   " + line); }), _cts.Token));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    item.Status = e.Message;
                    failed++;
                    continue;
                }
                item.Status = WingetParser.DescribeExitCode(code);
                if (WingetParser.IsSuccess(code))
                {
                    ok++;
                    item.IsChecked = false;
                }
                else
                {
                    failed++;
                }
            }
            Progress = 100;
            StatusText = $"Installed {Format.Plural(ok, "app")}" + (failed > 0 ? $", {failed} failed — see each app's status." : ".");
            SoundService.Play(failed == 0 ? UiSound.Success : UiSound.Warning);
            services.NotifyProgramsChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
            foreach (var a in chosen.Where(a => a.Status == "Installing…")) a.Status = "Stopped";
        }
        finally
        {
            IsInstalling = IsLoading = false;
        }
    }

    private static bool IsSpinner(string line) => line.Trim().Length <= 1 || line.Contains('█') || line.Contains('▒');

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
