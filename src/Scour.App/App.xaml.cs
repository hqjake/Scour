using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.App.ViewModels;
using Scour.App.Views;
using Scour.Core.Schedule;

namespace Scour.App;

public partial class App : Application
{
    private AppServices? _services;
    private TrayService? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private DispatcherTimer? _diskTimer;
    private GameModeHotkey? _hotkey;
    private bool _sessionEnding;

    /// <summary>Set when the user really wants to quit (tray "Exit"), so closing the window doesn't just hide it.</summary>
    public bool ExitRequested { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => _sessionEnding = true;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Background task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("Crash", ex);
        };
        // WPF formats and parses bound numbers and dates as en-US unless told otherwise, so "1,5" typed on a
        // German PC was rejected and dates showed in US order.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        Scour.Core.Shortcuts.LnkParser.AnsiEncoding = ProcessRunner.AnsiEncoding;
        _services = new AppServices();

        if (e.Args.Contains("--autoclean", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await RunScheduledCleanAsync(_services);
            Shutdown();
            return;
        }

        var shotIndex = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        bool screenshots = shotIndex >= 0;
        if (screenshots) Dialogs.Headless = true;

        SoundService.Enabled = _services.Settings.SoundsEnabled && !screenshots;
        SoundService.Initialize();
        ThemeService.Apply(_services.Settings.DarkTheme);

        // Every button and toggle clicks; every window gets the themed title bar and neon border.
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => SoundService.Play(UiSound.Click)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ThemeService.StyleWindow((Window)s)));

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _vm = new MainViewModel(_services);
        _vm.QuickClean = () => QuickCleanAsync().Forget("Quick clean");
        _window = new MainWindow { DataContext = _vm, SkipIntro = screenshots };
        MainWindow = _window;
        _window.Closing += OnMainWindowClosing;
        _window.Closed += (_, _) => Quit(closeWindow: false);

        if (screenshots)
        {
            var shotDir = shotIndex + 1 < e.Args.Length ? e.Args[shotIndex + 1] : Path.Combine(AppContext.BaseDirectory, "screenshots");
            ScreenshotRunner.OpenLog(shotDir);
            ScreenshotRunner.EnsureScreenSize();
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = 0;
            _window.Top = 0;
            _window.Width = 1440;
            _window.Height = 900;
            _window.Show();
            await ScreenshotRunner.RunAsync(_window, _vm, shotDir);
            Quit();
            return;
        }

        if (_services.Settings.KeepInTray || _services.Settings.StartInTray) EnsureTray();
        bool startHidden = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase) && _tray is not null;
        if (!startHidden) _window.Show();

        StartDiskWatch();
        _vm.GameMode.Changed += on => _tray?.SetGameMode(on);
        _hotkey = new GameModeHotkey(ToggleGameMode);
        UpdateService.CleanUpAsync().Forget("Removing the previous version", report: false);
        if (_services.Settings.CheckForUpdates) CheckForUpdatesLaterAsync().Forget("Checking for updates", report: false);
        // Re-register scheduled tasks so they follow Scour if its exe was moved.
        if (_services.Settings.AutoClean != CleaningSchedule.Off || _services.Settings.StartInTray)
            ScheduleService.ApplyAsync(_services.Settings).Forget("Updating scheduled tasks", report: false);
    }

    private async Task CheckForUpdatesLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)); // don't compete with startup work
        if (_vm is not null) await _vm.CheckForUpdatesAsync(silent: true);
    }

    /// <summary>The new version has started; get out of its way.</summary>
    public void QuitForUpdate() => Quit();

    public void EnsureTray()
    {
        if (_tray is not null || _services is null) return;
        _tray = new TrayService(
            open: ShowMainWindow,
            quickClean: () => QuickCleanAsync().Forget("Quick clean"),
            checkHealth: () => { ShowMainWindow(); if (_vm is not null) { _vm.Navigate(_vm.Dashboard); _vm.Dashboard.CheckHealthCommand.ExecuteAsync(null).Forget("Health check"); } },
            analyzeDisk: () => { ShowMainWindow(); _vm?.Navigate<DiskAnalyzerViewModel>(); },
            toggleGameMode: ToggleGameMode,
            exit: () => { AskToEndGameMode(); Quit(); });
        _tray.SetGameMode(_vm?.GameMode.IsOn == true);
    }

    public void RemoveTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ExitRequested || _services is null) return;
        if (!_services.Settings.KeepInTray)
        {
            AskToEndGameMode();
            return;
        }
        EnsureTray();
        e.Cancel = true;
        _window!.Hide();
        if (!_services.Settings.TrayHintShown)
        {
            _services.Settings.TrayHintShown = true;
            _services.Settings.Save();
            _tray?.Notify("Scour is still running", "It's in the notification area. Right-click the icon for quick actions, or turn this off in Settings.");
        }
    }

    /// <summary>
    /// Game mode outlives Scour (it's only settings), but silenced notifications shouldn't be forgotten. Asked
    /// only when the user closes Scour, never during an update or when Windows signs out.
    /// </summary>
    private void AskToEndGameMode()
    {
        if (_vm?.GameMode.IsOn != true || Dialogs.Headless || _sessionEnding) return;
        if (Dialogs.Confirm("Game mode is still on. Turn it off before closing Scour?\n\nChoose No to keep it on; open Scour again to turn it off later."))
            _services?.GameMode.Disable();
    }

    /// <summary>From the tray menu or Ctrl+Alt+G (possibly from inside a game, so a sound is the feedback).</summary>
    private void ToggleGameMode()
    {
        if (_vm is null || _vm.GameMode.IsBusy) return;
        _vm.GameMode.ToggleCommand.ExecuteAsync(null).Forget("Switching Game mode");
    }

    private void Quit(bool closeWindow = true)
    {
        if (ExitRequested) return;
        ExitRequested = true;
        _hotkey?.Dispose();
        _diskTimer?.Stop();
        RemoveTray();
        if (closeWindow) _window?.Close();
        Shutdown();
    }

    private async Task QuickCleanAsync()
    {
        if (_services is null) return;
        var s = _services;
        var result = await Task.Run(() => AutoCleaner.Run(s.Junk, AutoCleaner.DefaultCategories(s.Junk), DateTime.UtcNow));
        s.Settings.LastCleanupUtc = DateTime.UtcNow;
        s.Settings.Save();
        SoundService.Play(UiSound.Success);
        var message = result.BytesFreed > 0 ? $"Freed {Format.Bytes(result.BytesFreed)}." : "Nothing to clean — already tidy.";
        if (_tray is not null && _window?.IsVisible != true) _tray.Notify("Quick clean finished", message);
        else Dialogs.Info("Quick clean finished. " + message);
    }

    private static async Task RunScheduledCleanAsync(AppServices s)
    {
        if (s.GameMode.IsOn) return; // never clean (and slow the disk) in the middle of a game; the next run catches up
        try
        {
            var result = await Task.Run(() => AutoCleaner.Run(s.Junk, AutoCleaner.DefaultCategories(s.Junk), DateTime.UtcNow));
            long freed = result.BytesFreed;
            if (s.Settings.AutoCleanRecycleBin)
            {
                var (bytes, _) = RecycleBin.Query();
                if (bytes > 0 && RecycleBin.Empty()) freed += bytes;
            }
            s.Settings.LastCleanupUtc = DateTime.UtcNow;
            s.Settings.Save();
            if (freed > 0) await TrayService.NotifyOnceAsync("Scour cleaned your PC", $"Freed {Format.Bytes(freed)} of junk.");
        }
        catch { /* scheduled runs stay silent on failure */ }
    }

    private void StartDiskWatch()
    {
        _diskTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _diskTimer.Tick += (_, _) => CheckDiskSpace();
        _diskTimer.Start();
        CheckDiskSpace();
    }

    private void CheckDiskSpace()
    {
        var s = _services?.Settings;
        if (s is null || !s.LowDiskAlerts || _tray is null || _services!.GameMode.IsOn) return;
        if (s.LastLowDiskAlertUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double pct = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
            if (pct >= 10 && drive.AvailableFreeSpace >= 5L << 30) return;
            s.LastLowDiskAlertUtc = DateTime.UtcNow;
            s.Save();
            _tray.Notify($"Low disk space on {drive.Name.TrimEnd('\\')}", $"Only {Format.Bytes(drive.AvailableFreeSpace)} left. Click to see what's using it.",
                () => { ShowMainWindow(); _vm?.Navigate<DiskAnalyzerViewModel>(); }, warning: true);
        }
        catch { /* drive query failed */ }
    }

    private static string? _lastError;
    private static DateTime _lastErrorTime;

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ErrorLog.Write("Unhandled", e.Exception);
        // An error raised during layout repeats on every pass; show it once, not an endless chain of boxes.
        var message = e.Exception.Message;
        if (message == _lastError && DateTime.UtcNow - _lastErrorTime < TimeSpan.FromSeconds(10)) return;
        _lastError = message;
        _lastErrorTime = DateTime.UtcNow;
        Dialogs.Error($"Something went wrong:\n\n{message}\n\nThe details were saved. To report this, use Settings → Create problem report.");
    }
}
