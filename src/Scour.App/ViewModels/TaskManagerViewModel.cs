using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;

namespace Scour.App.ViewModels;

public sealed partial class ProcessRowViewModel(ProcessSample s) : ObservableObject
{
    public int Pid { get; } = s.Pid;
    public string Name { get; } = s.Name;
    public string? Path { get; } = s.Path;
    public DateTime StartTime { get; } = s.StartTime;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CpuText))] private double _cpu = s.CpuPercent;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MemoryText))] private long _memory = s.PrivateBytes;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DiskText))] private double _disk = s.IoBytesPerSec;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NetworkText))] private double _network = s.NetBytesPerSec;

    public string CpuText => Cpu < 0.05 ? "0%" : $"{Cpu:0.#}%";
    public string MemoryText => Format.Bytes(Memory);
    public string DiskText => Rate(Disk);
    public string NetworkText => Rate(Network);

    private static string Rate(double v) => v < 1 ? "0" : Format.Bytes((long)v) + "/s";

    public void Update(ProcessSample s)
    {
        Cpu = s.CpuPercent;
        Memory = s.PrivateBytes;
        Disk = s.IoBytesPerSec;
        Network = s.NetBytesPerSec;
    }
}

public sealed partial class ServiceRowViewModel(ServiceInfo info) : ObservableObject
{
    public ServiceInfo Info { get; } = info;
    public string Name => Info.Name;
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;
    public string Company => Info.Company;
    public string Status => Info.Status switch
    {
        ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.Paused => "Paused",
        _ => "Changing…",
    };
    public string Startup => Info.Startup switch
    {
        StartupKind.AutomaticDelayed => "Automatic (delayed)",
        StartupKind.Boot or StartupKind.System => "Driver",
        var k => k.ToString(),
    };
}

public sealed record LockRow(FileLock Lock, string? Service)
{
    public string Process => $"{Lock.ProcessName} ({Lock.Pid})" + (Service is null ? "" : $" · service {Service}");
    public string Path => Lock.LockedPath;
}

/// <summary>Processes with live usage, services, and "what's locking this file".</summary>
public sealed partial class TaskManagerViewModel : PageViewModel
{
    /// <summary>Ending these takes Windows down with them (blue screen or sign-out).</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "dwm", "fontdrvhost",
        "Memory Compression", "Secure System", "svchost", "MsMpEng", "audiodg",
    };

    private readonly ProcessMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<(int, DateTime), ProcessRowViewModel> _rows = new();
    private bool _sampling;

    public TaskManagerViewModel()
    {
        _timer.Tick += (_, _) => SampleAsync().Forget("Reading processes", report: false);
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Processes);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.Cpu), ListSortDirection.Descending));
        view.IsLiveSorting = true;
        foreach (var p in new[] { nameof(ProcessRowViewModel.Cpu), nameof(ProcessRowViewModel.Memory), nameof(ProcessRowViewModel.Disk), nameof(ProcessRowViewModel.Network) })
            view.LiveSortingProperties.Add(p);
        view.Filter = o => Filter.Length == 0 || o is ProcessRowViewModel r &&
            (r.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString() == Filter);
        ProcessView = view;

        ServiceView = CollectionViewSource.GetDefaultView(Services);
        ServiceView.Filter = o => o is ServiceRowViewModel r && (!HideMicrosoft || !r.Info.IsMicrosoft) &&
            (ServiceFilter.Length == 0 || r.DisplayName.Contains(ServiceFilter, StringComparison.OrdinalIgnoreCase) ||
             r.Name.Contains(ServiceFilter, StringComparison.OrdinalIgnoreCase));
    }

    public override string Title => "Task manager";
    public override string Glyph => "";
    public override string Group => "System";

    // ---------------------------------------------------------------- processes

    public ObservableCollection<ProcessRowViewModel> Processes { get; } = [];
    public ICollectionView ProcessView { get; }

    [ObservableProperty] private ProcessRowViewModel? _selected;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _networkNote = "";

    partial void OnFilterChanged(string value) => ProcessView.Refresh();

    protected override async Task LoadAsync()
    {
        IsLoading = true;
        try { await SampleAsync(); }
        finally { IsLoading = false; }
    }

    public override void OnShown()
    {
        // Per-process network use comes from a kernel trace; it only runs while this page is open.
        if (!_monitor.Network.IsRunning) _monitor.Network.Start();
        NetworkNote = _monitor.Network.IsRunning ? "" : "Network use per app isn't available: " + _monitor.Network.Error;
        _timer.Start();
    }

    public override void OnHidden()
    {
        _timer.Stop();
        _monitor.Network.Dispose();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var (samples, system) = await Task.Run(_monitor.Sample);
            var seen = new HashSet<(int, DateTime)>();
            foreach (var s in samples)
            {
                var key = (s.Pid, s.StartTime);
                seen.Add(key);
                if (_rows.TryGetValue(key, out var row)) row.Update(s);
                else
                {
                    row = new ProcessRowViewModel(s);
                    _rows[key] = row;
                    Processes.Add(row);
                }
            }
            foreach (var gone in _rows.Where(kv => !seen.Contains(kv.Key)).ToList())
            {
                _rows.Remove(gone.Key);
                Processes.Remove(gone.Value);
            }
            Summary = $"{system.ProcessCount} processes · CPU {system.CpuPercent:0}% · memory {Format.Bytes((long)system.MemoryUsed)} of {Format.Bytes((long)system.MemoryTotal)}";
        }
        finally
        {
            _sampling = false;
        }
    }

    private bool Refuse(ProcessRowViewModel p)
    {
        if (!Critical.Contains(p.Name) && p.Pid > 4) return false;
        Dialogs.Error($"{p.Name} is part of Windows. Ending it would crash or sign out the PC, so Scour won't.");
        return true;
    }

    [RelayCommand]
    private async Task EndTaskAsync(ProcessRowViewModel? p)
    {
        p ??= Selected;
        if (p is null || Refuse(p)) return;
        if (!Dialogs.Confirm($"End {p.Name} (PID {p.Pid})? Unsaved work in it is lost.")) return;
        await Act(() => ProcessControl.Kill(p.Pid, tree: false), $"Ended {p.Name}.");
    }

    [RelayCommand]
    private async Task EndTreeAsync(ProcessRowViewModel? p)
    {
        p ??= Selected;
        if (p is null || Refuse(p)) return;
        if (!Dialogs.Confirm($"End {p.Name} and every program it started? Unsaved work in them is lost.")) return;
        await Act(() => ProcessControl.Kill(p.Pid, tree: true), $"Ended {p.Name} and its child processes.");
    }

    [RelayCommand]
    private Task SetPriorityAsync(string? level)
    {
        var p = Selected;
        if (p is null || level is null || !Enum.TryParse<ProcessPriorityClass>(level, out var priority)) return Task.CompletedTask;
        if (priority == ProcessPriorityClass.RealTime) return Task.CompletedTask; // can starve the mouse and keyboard
        return Act(() => ProcessControl.SetPriority(p.Pid, priority), $"{p.Name} now runs at {level} priority.");
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (Selected?.Path is { } path) Shell.OpenFolder(path);
    }

    [RelayCommand]
    private void SearchOnline()
    {
        if (Selected is { } p) Shell.SearchWeb(p.Name + ".exe");
    }

    [RelayCommand]
    private async Task CheckOnlineAsync()
    {
        if (Selected?.Path is not { } path) return;
        // Only the file's fingerprint is sent, never the file.
        await Act(() => ProcessControl.OpenVirusTotal(path), "Opened the VirusTotal report for this file's fingerprint.");
    }

    private async Task Act(Action action, string done)
    {
        try
        {
            await Task.Run(action);
            StatusText = done;
            await SampleAsync();
        }
        catch (Exception e)
        {
            StatusText = "Couldn't do that: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
    }

    // ---------------------------------------------------------------- services

    public ObservableCollection<ServiceRowViewModel> Services { get; } = [];
    public ICollectionView ServiceView { get; }

    [ObservableProperty] private ServiceRowViewModel? _selectedService;
    [ObservableProperty] private string _serviceFilter = "";
    [ObservableProperty] private bool _hideMicrosoft = true;
    [ObservableProperty] private bool _isLoadingServices;
    private bool _servicesLoaded;

    partial void OnServiceFilterChanged(string value) => ServiceView.Refresh();
    partial void OnHideMicrosoftChanged(bool value) => ServiceView.Refresh();

    /// <summary>Called when the Services tab is first shown.</summary>
    public void EnsureServices()
    {
        if (_servicesLoaded) return;
        _servicesLoaded = true;
        RefreshServicesAsync().Forget("Listing services", report: false);
    }

    [RelayCommand]
    private async Task RefreshServicesAsync()
    {
        IsLoadingServices = true;
        try
        {
            var list = await Task.Run(ServiceManager.List);
            Services.Clear();
            foreach (var s in list) Services.Add(new ServiceRowViewModel(s));
            StatusText = $"{list.Count} services, {list.Count(s => !s.IsMicrosoft)} not from Microsoft.";
        }
        catch (Exception e)
        {
            ErrorLog.Write("Listing services", e);
            StatusText = "Couldn't list services: " + e.Message;
        }
        finally
        {
            IsLoadingServices = false;
        }
    }

    private bool ConfirmService(ServiceRowViewModel s, string action) =>
        !s.Info.IsMicrosoft ||
        Dialogs.ConfirmWarning($"{s.DisplayName} is part of Windows. {action} it can break Windows features that depend on it. Continue?");

    [RelayCommand]
    private Task StartServiceAsync() => ServiceAction(s => ServiceManager.StartAsync(s.Name), "Started", null);

    [RelayCommand]
    private Task StopServiceAsync() => ServiceAction(s => ServiceManager.StopAsync(s.Name), "Stopped", "Stopping");

    [RelayCommand]
    private Task RestartServiceAsync() => ServiceAction(s => ServiceManager.RestartAsync(s.Name), "Restarted", "Restarting");

    [RelayCommand]
    private Task SetStartupAsync(string? kind)
    {
        if (!Enum.TryParse<StartupKind>(kind, out var k)) return Task.CompletedTask;
        return ServiceAction(s => ServiceManager.SetStartupAsync(s.Name, k), $"Set to {kind} for",
                             k == StartupKind.Disabled ? "Disabling" : null);
    }

    private async Task ServiceAction(Func<ServiceRowViewModel, Task> action, string done, string? riskyVerb)
    {
        if (SelectedService is not { } s) return;
        if (riskyVerb is not null && !ConfirmService(s, riskyVerb)) return;
        try
        {
            await action(s);
            StatusText = $"{done} {s.DisplayName}.";
        }
        catch (Exception e)
        {
            StatusText = $"{s.DisplayName}: {(e.InnerException ?? e).Message}";
            SoundService.Play(UiSound.Warning);
        }
        await RefreshServicesAsync();
        SelectedService = Services.FirstOrDefault(x => x.Name == s.Name);
    }

    // ---------------------------------------------------------------- unlock

    public ObservableCollection<LockRow> Locks { get; } = [];
    [ObservableProperty] private LockRow? _selectedLock;
    [ObservableProperty] private string _lockTarget = "";
    [ObservableProperty] private bool _isFindingLocks;
    [ObservableProperty] private string _lockStatus = "Choose a file or folder that Windows says is \"in use\" to see which programs hold it open.";

    [RelayCommand]
    private Task PickLockedFileAsync() =>
        Dialogs.PickFile("Choose the locked file", "All files (*.*)|*.*") is { } f ? FindLocksAsync(f) : Task.CompletedTask;

    [RelayCommand]
    private Task PickLockedFolderAsync() =>
        Dialogs.PickFolder("Choose the locked folder") is { } f ? FindLocksAsync(f) : Task.CompletedTask;

    public async Task FindLocksAsync(string target)
    {
        LockTarget = target;
        IsFindingLocks = true;
        Locks.Clear();
        LockStatus = "Looking…";
        try
        {
            var (processes, handles) = await Task.Run(() =>
            {
                var files = LockFinder.ExpandTargets(target);
                var procs = LockFinder.FindProcesses(files);
                IReadOnlySet<int>? pids = procs.Count > 0 ? procs.Select(p => p.Pid).ToHashSet() : null;
                return (procs, LockFinder.FindHandles(target, pids));
            });
            foreach (var h in handles)
                Locks.Add(new LockRow(h, processes.FirstOrDefault(p => p.Pid == h.Pid)?.ServiceName));
            // Restart Manager can name a process whose exact handle isn't visible (e.g. a memory-mapped file).
            var extra = processes.Where(p => handles.All(h => h.Pid != p.Pid)).Select(p => p.Name).Distinct().ToList();
            LockStatus = Locks.Count == 0 && extra.Count == 0
                ? "Nothing has it open now. If deleting still fails, it's a permissions problem rather than a lock."
                : $"{Format.Plural(Locks.Count, "open handle")} found." +
                  (extra.Count > 0 ? $" Also used by {string.Join(", ", extra)} (end the program to release it)." : "");
        }
        catch (Exception e)
        {
            ErrorLog.Write("Finding file locks", e);
            LockStatus = "Couldn't check: " + e.Message;
        }
        finally
        {
            IsFindingLocks = false;
        }
    }

    [RelayCommand]
    private async Task CloseHandleAsync()
    {
        if (SelectedLock is not { } l) return;
        if (!Dialogs.ConfirmWarning($"Force {l.Lock.ProcessName} to let go of this file?\n\n{l.Path}\n\nThe program may lose unsaved changes to it or stop working until it's restarted.")) return;
        try
        {
            await Task.Run(() => LockFinder.CloseHandle(l.Lock));
            Locks.Remove(l);
            LockStatus = "Released. You can now move or delete the file.";
        }
        catch (Exception e)
        {
            LockStatus = "Couldn't release it: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task EndLockingProcessAsync()
    {
        if (SelectedLock is not { } l) return;
        if (Critical.Contains(l.Lock.ProcessName))
        {
            Dialogs.Error($"{l.Lock.ProcessName} is part of Windows and can't be ended. Restart the PC to release the file.");
            return;
        }
        if (!Dialogs.Confirm($"End {l.Lock.ProcessName} (PID {l.Lock.Pid})? Unsaved work in it is lost.")) return;
        try
        {
            await Task.Run(() => ProcessControl.Kill(l.Lock.Pid, tree: false));
            await FindLocksAsync(LockTarget);
        }
        catch (Exception e)
        {
            LockStatus = "Couldn't end it: " + e.Message;
        }
    }
}
