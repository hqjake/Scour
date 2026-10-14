using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Updates;

namespace Scour.App.ViewModels;

public sealed partial class UpdateItemViewModel(WingetPackage package, bool ignored) : ObservableObject
{
    public WingetPackage Package { get; } = package;
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Package.Version;
    public string Available => Package.Available;
    public string Source => Package.Source;
    public string Note => Package.RequiresExplicit ? "Pinned / manual only" : "";

    [ObservableProperty] private bool _isChecked = !ignored && !package.RequiresExplicit;
    [ObservableProperty] private bool _isIgnored = ignored;
    [ObservableProperty] private string _status = ignored ? "Ignored" : "";
}

public sealed partial class AppUpdatesViewModel(AppServices services) : PageViewModel
{
    private CancellationTokenSource? _cts;

    public override string Title => "App updates";
    public override string Glyph => "";
    public override string Group => "Programs";

    public ObservableCollection<UpdateItemViewModel> Updates { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private bool _includeUnknown;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _progress;

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Checking for updates…";
        try
        {
            var ignored = services.Settings.IgnoredUpdates.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = await WingetService.ListUpgradesAsync(IncludeUnknown);
            Updates.Clear();
            foreach (var p in list.OrderBy(p => ignored.Contains(p.Id)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                Updates.Add(new UpdateItemViewModel(p, ignored.Contains(p.Id)));
            int active = Updates.Count(u => !u.IsIgnored);
            StatusText = active == 0 ? "Everything is up to date." : $"{Format.Plural(active, "update")} available.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var u in Updates.Where(u => !u.IsIgnored)) u.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var u in Updates) u.IsChecked = false;
    }

    [RelayCommand]
    private void ToggleIgnore(UpdateItemViewModel? item)
    {
        if (item is null) return;
        var list = services.Settings.IgnoredUpdates;
        if (item.IsIgnored)
        {
            list.RemoveAll(id => id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            item.IsIgnored = false;
            item.Status = "";
        }
        else
        {
            list.Add(item.Id);
            item.IsIgnored = true;
            item.IsChecked = false;
            item.Status = "Ignored";
        }
        services.Settings.Save();
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        var chosen = Updates.Where(u => u.IsChecked && !u.IsIgnored).ToList();
        if (chosen.Count == 0) return;
        if (!Dialogs.Confirm($"Update {Format.Plural(chosen.Count, "app")}?\n\nUpdates install silently. Close the apps being updated first; some may ask to restart.")) return;

        _cts = new CancellationTokenSource();
        IsUpdating = IsLoading = true;
        Log.Clear();
        int ok = 0, failed = 0;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            for (int i = 0; i < chosen.Count; i++)
            {
                var item = chosen[i];
                Progress = 100.0 * i / chosen.Count;
                StatusText = $"Updating {item.Name} ({i + 1} of {chosen.Count})…";
                item.Status = "Updating…";
                Log.Add($"── {item.Name} {item.Version} → {item.Available}");
                int code;
                try
                {
                    code = await Task.Run(() => WingetService.UpgradeAsync(item.Package,
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
            StatusText = $"Updated {Format.Plural(ok, "app")}" + (failed > 0 ? $", {failed} failed — see each app's status." : ".");
            SoundService.Play(failed == 0 ? UiSound.Success : UiSound.Warning);
            services.NotifyProgramsChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped.";
        }
        finally
        {
            IsUpdating = IsLoading = false;
        }
    }

    private static bool IsSpinner(string line) => line.Trim().Length <= 1 || line.Contains('█') || line.Contains('▒');

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
