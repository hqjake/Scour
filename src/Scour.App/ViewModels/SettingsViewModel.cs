using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Leftovers;

namespace Scour.App.ViewModels;

public sealed partial class SettingsViewModel(AppServices services) : PageViewModel
{
    private AppSettings S => services.Settings;

    public override string Title => "Settings";
    public override string Glyph => "";

    public IReadOnlyList<ScanMode> ScanModes { get; } = [ScanMode.Safe, ScanMode.Moderate, ScanMode.Advanced];

    public ScanMode DefaultScanMode
    {
        get => S.DefaultScanMode;
        set { S.DefaultScanMode = value; Save(); }
    }

    public bool CreateRestorePoint
    {
        get => S.CreateRestorePoint;
        set { S.CreateRestorePoint = value; Save(); }
    }

    public bool FilesToRecycleBin
    {
        get => S.FilesToRecycleBin;
        set { S.FilesToRecycleBin = value; Save(); }
    }

    public bool BackupRegistry
    {
        get => S.BackupRegistry;
        set { S.BackupRegistry = value; Save(); }
    }

    public bool QuickUninstallSilent
    {
        get => S.QuickUninstallSilent;
        set { S.QuickUninstallSilent = value; Save(); }
    }

    public bool ShowSystemComponents
    {
        get => S.ShowSystemComponents;
        set { S.ShowSystemComponents = value; Save(); services.NotifyProgramsChanged(); }
    }

    public bool ShowUpdates
    {
        get => S.ShowUpdates;
        set { S.ShowUpdates = value; Save(); services.NotifyProgramsChanged(); }
    }

    public bool DarkTheme
    {
        get => S.DarkTheme;
        set { S.DarkTheme = value; Save(); ThemeService.Apply(value); }
    }

    public bool SoundsEnabled
    {
        get => S.SoundsEnabled;
        set { S.SoundsEnabled = value; SoundService.Enabled = value; Save(); }
    }

    // ---- background

    public IReadOnlyList<Scour.Core.Schedule.CleaningSchedule> Schedules { get; } =
        [Scour.Core.Schedule.CleaningSchedule.Off, Scour.Core.Schedule.CleaningSchedule.Daily, Scour.Core.Schedule.CleaningSchedule.Weekly];
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    [ObservableProperty] private string _scheduleStatus = "";

    public Scour.Core.Schedule.CleaningSchedule AutoClean
    {
        get => S.AutoClean;
        set { S.AutoClean = value; Save(); _ = ApplyScheduleAsync(); }
    }

    public DayOfWeek AutoCleanDay
    {
        get => S.AutoCleanDay;
        set { S.AutoCleanDay = value; Save(); _ = ApplyScheduleAsync(); }
    }

    public int AutoCleanHour
    {
        get => S.AutoCleanHour;
        set { S.AutoCleanHour = value; Save(); _ = ApplyScheduleAsync(); }
    }

    public bool AutoCleanRecycleBin
    {
        get => S.AutoCleanRecycleBin;
        set { S.AutoCleanRecycleBin = value; Save(); }
    }

    public bool KeepInTray
    {
        get => S.KeepInTray;
        set
        {
            S.KeepInTray = value;
            Save();
            if (System.Windows.Application.Current is App app)
            {
                if (value) app.EnsureTray();
                else if (!S.StartInTray) app.RemoveTray();
            }
        }
    }

    public bool StartInTray
    {
        get => S.StartInTray;
        set { S.StartInTray = value; Save(); _ = ApplyScheduleAsync(); }
    }

    public bool LowDiskAlerts
    {
        get => S.LowDiskAlerts;
        set { S.LowDiskAlerts = value; Save(); }
    }

    public string LastCleanupText => S.LastCleanupUtc is { } t ? $"Last cleanup: {t.ToLocalTime():g}" : "No cleanup yet.";

    private async Task ApplyScheduleAsync()
    {
        ScheduleStatus = "Updating Windows Task Scheduler…";
        string? error;
        try { error = await ScheduleService.ApplyAsync(S); }
        catch (Exception e) { error = e.Message; }
        ScheduleStatus = error is null ? "Saved." : "Couldn't update the schedule: " + error;
    }

    /// <summary>Re-reads every setting (after changes made elsewhere, e.g. the sidebar toggles).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    // ---- updates

    /// <summary>Set by the main view model; returns a sentence describing the result.</summary>
    public Func<Task<string>>? CheckForUpdates { get; set; }

    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _isCheckingForUpdates;

    public bool AutoCheckForUpdates
    {
        get => S.CheckForUpdates;
        set { S.CheckForUpdates = value; Save(); }
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (CheckForUpdates is null) return;
        IsCheckingForUpdates = true;
        UpdateCheckStatus = "Checking…";
        UpdateCheckStatus = await CheckForUpdates();
        IsCheckingForUpdates = false;
    }

    public string DataFolder => services.Folders.AppDataDir;
    public string Version => UpdateService.CurrentVersion.ToString(3);

    private void Save()
    {
        S.Save();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        System.IO.Directory.CreateDirectory(DataFolder);
        Shell.OpenFolder(DataFolder);
    }
}
