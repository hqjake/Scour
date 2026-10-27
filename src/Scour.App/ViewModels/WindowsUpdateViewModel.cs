using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Updates;

namespace Scour.App.ViewModels;

public sealed record UpdateHistoryRow(UpdateHistoryEntry Entry)
{
    public string Title => Entry.Title;
    public string Date => Entry.DateUtc == default ? "" : Entry.DateUtc.ToLocalTime().ToString("g");
    public string Result => Entry.Result switch
    {
        UpdateResult.Succeeded => Entry.IsUninstall ? "Removed" : "Installed",
        UpdateResult.SucceededWithErrors => "Installed with errors",
        UpdateResult.Failed => "Failed " + Entry.ErrorCode,
        UpdateResult.Aborted => "Cancelled",
        UpdateResult.InProgress => "In progress",
        _ => "Not started",
    };
    public bool IsFailure => Entry.IsFailure;
}

public sealed partial class PendingUpdateRow(PendingUpdate update) : ObservableObject
{
    public PendingUpdate Update { get; } = update;
    public string Title => Update.Title;
    public string Kind => Update.IsDriver ? "Driver" : "Update";
    public string Size => Update.SizeBytes > 0 ? Format.Bytes(Update.SizeBytes) : "";
    [ObservableProperty] private bool _isHidden = update.IsHidden;
    public string State => IsHidden ? "Hidden" : "Waiting";
    partial void OnIsHiddenChanged(bool value) => OnPropertyChanged(nameof(State));
}

/// <summary>Pause Windows Update, see what it installed (and what failed), and hide updates you don't want.</summary>
public sealed partial class WindowsUpdateViewModel(AppServices services) : PageViewModel
{
    private readonly WindowsUpdatePause _pause = new(services.Registry);

    public override string Title => "Windows Update";
    public override string Glyph => "";
    public override string Group => "System";

    public int[] PauseOptions { get; } = [1, 3, 7, 14, 21, 35];
    public ObservableCollection<UpdateHistoryRow> History { get; } = [];
    public ObservableCollection<PendingUpdateRow> Pending { get; } = [];

    [ObservableProperty] private int _pauseDays = 7;
    [ObservableProperty] private string _pauseText = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _pendingText = "Press Check to ask Windows Update what it wants to install.";
    [ObservableProperty] private string _historySummary = "";
    [ObservableProperty] private PendingUpdateRow? _selectedPending;
    [ObservableProperty] private UpdateHistoryRow? _selectedHistory;

    protected override async Task LoadAsync()
    {
        ShowPause();
        await LoadHistoryAsync();
    }

    private void ShowPause()
    {
        var until = _pause.PausedUntilUtc(DateTime.UtcNow);
        IsPaused = until is not null;
        PauseText = until is { } u ? $"Updates are paused until {u.ToLocalTime():f}." : "Windows Update runs normally.";
    }

    [RelayCommand]
    private void Pause()
    {
        try
        {
            var until = _pause.Pause(PauseDays, DateTime.UtcNow);
            ShowPause();
            ChangeHistory.Superseded(ChangeHistory.UpdatePause); // a new pause replaces the old one
            ChangeHistory.Record("Windows Update", $"Paused updates until {until.ToLocalTime():g}", null,
                                 new Scour.Core.Removal.UndoInfo(ChangeHistory.UpdatePause, ""));
            StatusText = $"Paused until {until.ToLocalTime():f}. Security updates wait too, so don't leave it paused for long.";
        }
        catch (Exception e)
        {
            StatusText = "Couldn't pause updates: " + e.Message;
        }
    }

    [RelayCommand]
    private void Resume()
    {
        try
        {
            _pause.Resume();
            ShowPause();
            ChangeHistory.Superseded(ChangeHistory.UpdatePause);
            ChangeHistory.Record("Windows Update", "Resumed updates");
            StatusText = "Updates resumed.";
        }
        catch (Exception e)
        {
            StatusText = "Couldn't resume updates: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        IsLoading = true;
        try
        {
            var history = await WindowsUpdateService.HistoryAsync();
            History.Clear();
            foreach (var h in history) History.Add(new UpdateHistoryRow(h));
            int failed = history.Count(h => h.IsFailure && h.DateUtc > DateTime.UtcNow.AddDays(-30));
            HistorySummary = history.Count == 0 ? "No updates recorded yet."
                : failed > 0 ? $"{Format.Plural(failed, "update")} failed in the last 30 days. Select one and press Search the error to find a fix."
                : "Everything in the last 30 days installed fine.";
        }
        catch (Exception e)
        {
            HistorySummary = "Couldn't read the update history: " + e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        IsChecking = true;
        PendingText = "Asking Windows Update… this can take a few minutes.";
        try
        {
            var pending = await WindowsUpdateService.PendingAsync();
            Pending.Clear();
            foreach (var p in pending.OrderBy(p => p.IsHidden).ThenByDescending(p => p.IsDriver)) Pending.Add(new PendingUpdateRow(p));
            PendingText = pending.Count == 0 ? "Nothing waiting to install." : $"{Format.Plural(pending.Count(p => !p.IsHidden), "update")} waiting" +
                          (pending.Any(p => p.IsHidden) ? $", {pending.Count(p => p.IsHidden)} hidden." : ".");
        }
        catch (Exception e)
        {
            PendingText = "Couldn't check: " + e.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private Task HideAsync() => SetHiddenAsync(true);

    [RelayCommand]
    private Task ShowAgainAsync() => SetHiddenAsync(false);

    private async Task SetHiddenAsync(bool hidden)
    {
        if (SelectedPending is not { } row || row.IsHidden == hidden) return;
        IsChecking = true;
        try
        {
            await WindowsUpdateService.SetHiddenAsync(row.Update.Id, hidden);
            row.IsHidden = hidden;
            ChangeHistory.Record("Windows Update", $"{(hidden ? "Hid" : "Showed again")} update \"{row.Title}\"",
                                 hidden ? "Show it again from the Windows Update page." : null);
            StatusText = hidden ? $"Hidden: Windows won't install \"{row.Title}\" until you show it again." : $"\"{row.Title}\" will be installed again.";
        }
        catch (Exception e)
        {
            StatusText = "Couldn't change it: " + e.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void SearchError()
    {
        if (SelectedHistory?.Entry is { ErrorCode: { } code } entry) Shell.SearchWeb($"Windows Update {code} {entry.Title}");
    }

    [RelayCommand]
    private void OpenSettings() => Shell.OpenUrl("ms-settings:windowsupdate");
}
