using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Scour.App.Platform;
using Scour.App.Services;

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

    private void Sample()
    {
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

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var drives = new List<DriveUsage>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                var used = d.TotalSize - d.TotalFreeSpace;
                var pct = d.TotalSize == 0 ? 0 : 100.0 * used / d.TotalSize;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local disk") : d.VolumeLabel;
                drives.Add(new DriveUsage(d.Name.TrimEnd('\\'), label, pct, $"{Format.Bytes(d.TotalFreeSpace)} free of {Format.Bytes(d.TotalSize)}"));
                if (string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                {
                    DiskPercent = pct;
                    DiskText = $"{Format.Bytes(d.TotalFreeSpace)} free";
                }
            }
            catch { /* drive went away */ }
        }
        if (drives.Count != Drives.Count || drives.Zip(Drives).Any(p => p.First != p.Second))
        {
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
        }
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
