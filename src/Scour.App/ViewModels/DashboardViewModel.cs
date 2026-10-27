using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Health;

namespace Scour.App.ViewModels;

public sealed record DriveUsage(string Name, string Label, double Percent, string Text);

public sealed record QuickAction(string Title, string Subtitle, string Glyph, Type PageType);

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly Action<PageViewModel> _navigate;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private long _idle, _kernel, _user;

    public DashboardViewModel(AppServices services, Action<PageViewModel> navigate)
    {
        _s = services;
        _navigate = navigate;
        _timer.Tick += (_, _) => Sample();
        MachineName = Environment.MachineName;
        OsName = ReadOsName();
        CpuName = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "";
        CpuName = CpuName.Trim();
    }

    public override string Title => "Dashboard";
    public override string Glyph => "";
    public override string Group => "Home";

    public string MachineName { get; }
    public string OsName { get; }
    public string CpuName { get; }

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private int _programCount;
    [ObservableProperty] private int _startupCount;
    [ObservableProperty] private string _greeting = "";

    public ObservableCollection<DriveUsage> Drives { get; } = [];
    public ObservableCollection<HealthIssue> Issues { get; } = [];

    [ObservableProperty] private double _healthScore;
    [ObservableProperty] private string _healthGrade = "Checking…";
    [ObservableProperty] private string _healthStatus = "";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private System.Windows.Media.Brush? _gradeBrush;

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        HealthGrade = "Checking…";
        var progress = new Progress<string>(s => HealthStatus = s + "…");
        try
        {
            var report = await HealthChecker.RunAsync(_s, progress);
            HealthScore = report.Score;
            HealthGrade = report.Grade;
            GradeBrush = System.Windows.Application.Current.TryFindResource(report.Score switch
            {
                >= 75 => "Scour.OkBrush",
                >= 50 => "Scour.WarnBrush",
                _ => "Scour.BadBrush",
            }) as System.Windows.Media.Brush;
            Issues.Clear();
            foreach (var i in report.Issues) Issues.Add(i);
            HealthStatus = report.Issues.Count == 0 ? "No problems found — your PC is in great shape." : $"{Format.Plural(report.Issues.Count, "thing")} to look at.";
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            HealthGrade = "Unavailable";
            HealthStatus = e.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void Fix(HealthIssue? issue)
    {
        if (issue is null) return;
        Type? page = issue.Fix switch
        {
            HealthFix.CleanJunk => typeof(JunkViewModel),
            HealthFix.FreeSpace => typeof(DiskAnalyzerViewModel),
            HealthFix.Startup => typeof(AutorunViewModel),
            HealthFix.Registry => typeof(RegistryCleanerViewModel),
            HealthFix.Updates => typeof(AppUpdatesViewModel),
            HealthFix.DriveHealth => typeof(SystemInfoViewModel),
            HealthFix.Battery => typeof(SystemInfoViewModel),
            _ => null,
        };
        if (page is not null)
        {
            if (ResolvePage?.Invoke(page) is { } p) _navigate(p);
            return;
        }
        if (issue.Fix == HealthFix.Antivirus)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
        else if (issue.Fix == HealthFix.Restart && Dialogs.Confirm("Restart the PC now? Save your work in other programs first."))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ProcessRunner.ToolPath("shutdown.exe"), "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
    }

    public IReadOnlyList<QuickAction> Actions { get; } =
    [
        new("Uninstall programs", "Remove apps and every trace they leave", "", typeof(ProgramsViewModel)),
        new("Clean junk", "Temp files, caches, logs and more", "", typeof(JunkViewModel)),
        new("Registry cleaner", "Fix entries pointing at missing files", "", typeof(RegistryCleanerViewModel)),
        new("Browser privacy", "Clear history and cookies safely", "", typeof(BrowserPrivacyViewModel)),
        new("Find duplicates", "Reclaim space wasted on copies", "", typeof(FileFinderViewModel)),
        new("Startup programs", "Speed up sign-in", "", typeof(AutorunViewModel)),
    ];

    public Func<Type, PageViewModel?>? ResolvePage { get; set; }

    [RelayCommand]
    private void Open(QuickAction? action)
    {
        if (action is null) return;
        var page = ResolvePage?.Invoke(action.PageType);
        if (page is not null) _navigate(page);
    }

    protected override async Task LoadAsync()
    {
        var h = DateTime.Now.Hour;
        Greeting = h < 5 ? "Working late" : h < 12 ? "Good morning" : h < 18 ? "Good afternoon" : "Good evening";
        Sample();
        _timer.Start();
        _ = CheckHealthAsync();
        await RefreshCountsAsync();
        _s.ProgramsChanged += () => _ = RefreshCountsAsync();
    }

    private async Task RefreshCountsAsync()
    {
        await Task.Delay(1500); // let the program list load first
        ProgramCount = _s.LastKnownPrograms.Count(p => !p.IsSystemComponent && !p.IsUpdate);
        try { StartupCount = await Task.Run(() => _s.Autorun.GetEntries().Count(e => e.Enabled)); }
        catch { StartupCount = 0; }
    }

    /// <summary>False while another page is shown; the live gauges then stop sampling.</summary>
    public bool IsShown { get; set; } = true;

    private void Sample()
    {
        // Nobody is looking (another page, minimised or in the tray): don't keep querying drives and CPU.
        if (!IsShown || System.Windows.Application.Current?.MainWindow is { } w && (!w.IsVisible || w.WindowState == System.Windows.WindowState.Minimized))
            return;
        if (ProcessNative.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            long dIdle = idle - _idle, dTotal = kernel - _kernel + (user - _user);
            if (_kernel != 0 && dTotal > 0) CpuPercent = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            (_idle, _kernel, _user) = (idle, kernel, user);
        }
        var mem = new ProcessNative.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<ProcessNative.MEMORYSTATUSEX>() };
        if (ProcessNative.GlobalMemoryStatusEx(ref mem))
        {
            var used = mem.ullTotalPhys - mem.ullAvailPhys;
            RamPercent = 100.0 * used / mem.ullTotalPhys;
            RamText = $"{Format.Bytes((long)used)} of {Format.Bytes((long)mem.ullTotalPhys)}";
        }

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";

        // Drives are read off the UI thread and less often: a sleeping external disk or a card reader can
        // take seconds to answer, which froze the whole window on every tick.
        if (_tick++ % 10 == 0 && !_drivesBusy) SampleDrivesAsync().Forget("Reading drives", report: false);
    }

    private int _tick;
    private bool _drivesBusy;

    private async Task SampleDrivesAsync()
    {
        _drivesBusy = true;
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var (drives, system) = await Task.Run(() => ReadDrives(systemRoot));
            if (system is { } sys)
            {
                DiskPercent = sys.Percent;
                DiskText = sys.Text;
            }
            if (drives.Count != Drives.Count || drives.Zip(Drives).Any(p => p.First != p.Second))
            {
                Drives.Clear();
                foreach (var d in drives) Drives.Add(d);
            }
        }
        finally
        {
            _drivesBusy = false;
        }
    }

    private static (List<DriveUsage> Drives, (double Percent, string Text)? System) ReadDrives(string? systemRoot)
    {
        var drives = new List<DriveUsage>();
        (double, string)? system = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                long total = d.TotalSize, free = d.TotalFreeSpace;
                var used = total - free;
                var pct = total == 0 ? 0 : 100.0 * used / total;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local disk") : d.VolumeLabel;
                drives.Add(new DriveUsage(d.Name.TrimEnd('\\'), label, pct, $"{Format.Bytes(free)} free of {Format.Bytes(total)}"));
                if (string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                    system = (pct, $"{Format.Bytes(free)} free");
            }
            catch { /* drive went away, or is locked (BitLocker) */ }
        }
        return (drives, system);
    }

    private static string ReadOsName()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", "Windows") as string ?? "Windows";
        var display = Registry.GetValue(key, "DisplayVersion", "") as string ?? "";
        // ProductName still says "Windows 10" on Windows 11.
        if (Environment.OSVersion.Version.Build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return string.IsNullOrEmpty(display) ? product : $"{product} {display}";
    }
}
