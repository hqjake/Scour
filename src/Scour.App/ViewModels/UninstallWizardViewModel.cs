using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Removal;
using Scour.Core.Tracing;

namespace Scour.App.ViewModels;

public enum WizardStage
{
    Preparing,
    Uninstalling,
    ReadyToScan,
    Scanning,
    Results,
    Removing,
    Done,
}

/// <summary>One program to remove: by its uninstaller, by a forced scan, or by a trace log.</summary>
public sealed class UninstallJob
{
    public required string Title { get; init; }
    public InstalledProgram? Program { get; init; }
    public ScanTarget? Target { get; init; }
    public bool RunUninstaller { get; init; }
    public TraceLog? Trace { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed partial class UninstallWizardViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IReadOnlyList<UninstallJob> _jobs;
    private readonly bool _quick;
    private CancellationTokenSource? _waitCts;
    private CancellationTokenSource? _scanCts;
    private ScanTarget? _target;
    private RemovalReport? _report;
    private bool _restorePointDone;

    public UninstallWizardViewModel(AppServices services, IReadOnlyList<UninstallJob> jobs, bool quick)
    {
        _s = services;
        _jobs = jobs;
        _quick = quick;
        _scanMode = services.Settings.DefaultScanMode;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.GroupName)));
        Items.CollectionChanged += (_, _) => UpdateSelectionSummary();
    }

    public event Action? CloseRequested;

    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<LeftoverItemViewModel> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public ObservableCollection<RemovalFailure> Failures { get; } = [];

    [ObservableProperty] private WizardStage _stage;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ScanMode _scanMode;
    [ObservableProperty] private bool _stillInstalled;
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private int _jobIndex;
    [ObservableProperty] private bool _hasFailures;

    public bool IsBatch => _jobs.Count > 1;
    public string JobCounter => $"Program {JobIndex + 1} of {_jobs.Count}";
    public bool HasMoreJobs => JobIndex < _jobs.Count - 1;
    public string NextButtonText => HasMoreJobs ? "Next program" : "Close";
    public bool IsBusy => Stage is WizardStage.Preparing or WizardStage.Uninstalling or WizardStage.Scanning or WizardStage.Removing;
    public int StepNumber => Stage switch
    {
        WizardStage.Preparing => 1,
        WizardStage.Uninstalling => 2,
        WizardStage.ReadyToScan or WizardStage.Scanning => 3,
        WizardStage.Results or WizardStage.Removing => 4,
        _ => 5,
    };

    partial void OnStageChanged(WizardStage value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StepNumber));
    }

    partial void OnJobIndexChanged(int value)
    {
        OnPropertyChanged(nameof(JobCounter));
        OnPropertyChanged(nameof(HasMoreJobs));
        OnPropertyChanged(nameof(NextButtonText));
    }

    private void Write(string line) => Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");

    public Task StartAsync() => RunJobAsync(_jobs[0]);

    private async Task RunJobAsync(UninstallJob job)
    {
        Items.Clear();
        Failures.Clear();
        HasFailures = false;
        StillInstalled = false;
        _report = null;
        Title = job.Title;
        Icon = job.Icon;
        Subtitle = job.Program?.Publisher ?? job.Target?.Publisher;
        Stage = WizardStage.Preparing;
        if (IsBatch) Write($"── {job.Title} ──");

        try
        {
            // Gather what the scanner needs while the program's files still exist.
            Status = "Analyzing the program…";
            _target = job.Program is not null
                ? await Task.Run(() => ScanTargetBuilder.FromProgram(job.Program, _s.LastKnownPrograms, _s.FileSystem, _s.SelfProtectedPaths()))
                : job.Target;

            if (job.RunUninstaller && job.Program is not null)
            {
                if (_s.Settings.CreateRestorePoint && !_restorePointDone)
                {
                    Status = "Creating a system restore point…";
                    Write("Creating a system restore point (this can take a minute)…");
                    var (_, message) = await RestorePoint.CreateAsync($"Scour: before uninstalling {job.Title}");
                    Write(message);
                    _restorePointDone = true;
                }
                await RunUninstallerAsync(job.Program);
            }

            if (job.Trace is not null)
            {
                await LoadTraceItemsAsync(job.Trace);
                return;
            }

            if (_target is null)
            {
                Stage = WizardStage.Done;
                ResultSummary = "Nothing to scan.";
                return;
            }

            Stage = WizardStage.ReadyToScan;
            Status = "";
            if (_quick)
            {
                if (StillInstalled)
                {
                    Write("Skipping the leftover scan because the program is still installed.");
                    Stage = WizardStage.Done;
                    ResultSummary = "The uninstaller didn't finish, so nothing else was removed.";
                    return;
                }
                await ScanAsync();
            }
        }
        catch (Exception e)
        {
            Write("Error: " + e.Message);
            Stage = WizardStage.Done;
            ResultSummary = "Something went wrong: " + e.Message;
        }
    }

    private async Task RunUninstallerAsync(InstalledProgram program)
    {
        bool quiet = _quick && _s.Settings.QuickUninstallSilent && UninstallCommandBuilder.SupportsQuiet(program);
        var cmd = UninstallCommandBuilder.Build(program, quiet, _s.Folders);
        if (cmd is null)
        {
            Write("This program has no uninstaller. Scour will look for its leftovers instead.");
            return;
        }

        Stage = WizardStage.Uninstalling;
        Status = quiet ? "Uninstalling silently…" : "Waiting for the program's uninstaller to finish…";
        Write("Running: " + cmd);
        _waitCts = new CancellationTokenSource();
        try
        {
            var exit = await ProcessRunner.RunUninstallerAsync(cmd, _waitCts.Token);
            if (_waitCts.IsCancellationRequested) Write("Stopped waiting for the uninstaller.");
            else Write(exit is null ? "The uninstaller finished." : $"The uninstaller finished (exit code {exit}).");
        }
        catch (Exception e)
        {
            Write("Could not run the uninstaller: " + e.Message);
        }
        finally
        {
            _waitCts.Dispose();
            _waitCts = null;
        }

        StillInstalled = _s.Registry.KeyExists(program.RegistryKey);
        if (StillInstalled)
            Write("The program is still listed as installed — the uninstaller may have been cancelled or need a restart.");
    }

    private async Task LoadTraceItemsAsync(TraceLog trace)
    {
        Stage = WizardStage.Scanning;
        Status = "Checking what the installation left on disk…";
        var existing = await Task.Run(() => trace.Items.Where(Exists).Select(Fresh).ToList());
        Write($"{existing.Count} of {trace.Items.Count} traced items still exist.");
        ShowResults(existing);
        if (_quick) await DeleteSelectedAsync();
    }

    private LeftoverItem Fresh(LeftoverItem i)
    {
        var copy = new LeftoverItem
        {
            Kind = i.Kind, Path = i.Path, RegistryKey = i.RegistryKey, ValueName = i.ValueName, ValueData = i.ValueData,
            Confidence = Confidence.Safe, Reasons = i.Reasons.ToList(), ServiceName = i.ServiceName,
        };
        try
        {
            if (copy.Kind == LeftoverKind.Folder) copy.SizeBytes = _s.FileSystem.GetDirectorySize(copy.Path);
            else if (copy.Kind == LeftoverKind.File) copy.SizeBytes = _s.FileSystem.GetFileSize(copy.Path);
        }
        catch { /* informational */ }
        return copy;
    }

    private bool Exists(LeftoverItem i) => i.Kind switch
    {
        LeftoverKind.RegistryKey => i.RegistryKey is { } k && _s.Registry.KeyExists(k),
        LeftoverKind.RegistryValue => i.RegistryKey is { } k && _s.Registry.ValueExists(k, i.ValueName ?? ""),
        LeftoverKind.Folder => _s.FileSystem.DirectoryExists(i.Path),
        _ => _s.FileSystem.FileExists(i.Path),
    };

    [RelayCommand]
    private void ContinueWithoutWaiting() => _waitCts?.Cancel();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_target is null) return;
        Stage = WizardStage.Scanning;
        Write($"Scanning for leftovers ({ScanMode} mode)…");
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<string>(s => Status = s + "…");
        try
        {
            var target = _target;
            var mode = ScanMode;
            var items = await Task.Run(() => _s.Scanner.Scan(target, mode, progress, _scanCts.Token));
            Write($"Found {Format.Plural(items.Count, "leftover")}.");
            ShowResults(items);
            SoundService.Play(UiSound.Scan);
            if (_quick) await DeleteSelectedAsync();
        }
        catch (OperationCanceledException)
        {
            Write("Scan cancelled.");
            Stage = WizardStage.ReadyToScan;
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    private void ShowResults(IEnumerable<LeftoverItem> items)
    {
        Items.Clear();
        foreach (var i in items)
        {
            var vm = new LeftoverItemViewModel(i);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LeftoverItemViewModel.IsChecked)) UpdateSelectionSummary();
            };
            Items.Add(vm);
        }
        Stage = WizardStage.Results;
        Status = "";
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var selected = Items.Where(i => i.IsChecked).ToList();
        long bytes = selected.Sum(i => i.Item.SizeBytes);
        SelectionSummary = Items.Count == 0
            ? "No leftovers found."
            : $"{selected.Count} of {Format.Plural(Items.Count, "item")} selected" + (bytes > 0 ? $" · {Format.Bytes(bytes)}" : "");
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var i in Items) i.IsChecked = i.Confidence == Confidence.Safe;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsChecked = false;
    }

    private bool CanDelete() => Items.Any(i => i.IsChecked);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Item).ToList();
        if (selected.Count == 0)
        {
            Finish("Nothing was selected, so nothing was deleted.");
            return;
        }
        if (!_quick)
        {
            var risky = selected.Count(i => i.Confidence != Confidence.Safe);
            var where = _s.Settings.FilesToRecycleBin ? "Files go to the Recycle Bin" : "Files are deleted permanently";
            var msg = $"Delete {Format.Plural(selected.Count, "item")}?\n\n{where}" +
                      (_s.Settings.BackupRegistry ? "; registry entries are backed up first." : ".") +
                      (risky > 0 ? $"\n\n{risky} of them are not marked Safe — make sure they really belong to this program." : "");
            if (!(risky > 0 ? Dialogs.ConfirmWarning(msg) : Dialogs.Confirm(msg))) return;
        }

        Stage = WizardStage.Removing;
        var job = _jobs[JobIndex];
        var progress = new Progress<string>(s => Status = s);
        RemovalReport report;
        try
        {
            report = await Task.Run(() => _s.CreateRemover().Remove(job.Title, selected, _s.RemovalOptions, DateTime.UtcNow, progress));
        }
        catch (Exception e)
        {
            // Without this the wizard stayed in "Removing" and refused to close.
            ErrorLog.Write("Removing leftovers", e);
            SoundService.Play(UiSound.Warning);
            Finish("Removal failed: " + e.Message);
            return;
        }
        _report = report;

        foreach (var f in report.Failed) Failures.Add(f);
        HasFailures = report.Failed.Count > 0;
        Write($"Removed {Format.Plural(report.Removed.Count, "item")}" + (report.BytesFreed > 0 ? $", freed {Format.Bytes(report.BytesFreed)}." : "."));
        if (report.Backup is not null) Write("Registry backup saved: " + report.Backup.Id);
        foreach (var f in report.Failed) Write($"Could not delete {f.Item.DisplayText}: {f.Error}");

        if (job.Trace is not null && report.Failed.Count == 0)
        {
            try { _s.Traces.Delete(job.Trace); Write("Trace log deleted."); }
            catch (Exception e) { Write("Could not delete the trace log: " + e.Message); }
        }

        var summary = $"Removed {Format.Plural(report.Removed.Count, "leftover")}";
        if (report.BytesFreed > 0) summary += $" and freed {Format.Bytes(report.BytesFreed)}";
        summary += ".";
        if (report.Failed.Count > 0) summary += $" {report.Failed.Count} could not be deleted (probably in use).";
        ChangeHistory.Record("Uninstall", $"Removed leftovers of {job.Title}", summary + (_s.Settings.FilesToRecycleBin ? " Files went to the Recycle Bin." : ""),
                             ChangeHistory.ForBackup(report.Backup));
        SoundService.Play(report.Failed.Count == 0 ? UiSound.Success : UiSound.Warning);
        Finish(summary);
    }

    [RelayCommand]
    private void SkipLeftovers() => Finish("Leftovers were left in place.");

    private void Finish(string summary)
    {
        ResultSummary = summary;
        Stage = WizardStage.Done;
        Status = "";
        if (_quick && !HasFailures && HasMoreJobs) NextAsync().Forget("Starting the next uninstall");
    }

    [RelayCommand]
    private void ScheduleOnReboot()
    {
        if (_report is null) return;
        int n = _s.CreateRemover().ScheduleOnReboot(_report.Failed);
        Write($"{Format.Plural(n, "item")} will be deleted at the next restart.");
        var remaining = _report.Failed.Where(f => f.Item.IsRegistry).ToList();
        Failures.Clear();
        foreach (var f in remaining) Failures.Add(f);
        HasFailures = remaining.Count > 0;
        ResultSummary += $" {Format.Plural(n, "item")} scheduled for deletion at restart.";
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (HasMoreJobs)
        {
            JobIndex++;
            await RunJobAsync(_jobs[JobIndex]);
        }
        else
        {
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void OpenItem(LeftoverItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            if (item.Item.RegistryKey is { } key) Shell.OpenRegistryKey(key);
            else Shell.OpenFolder(item.Item.Path);
        }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    /// <summary>Called by the window when the user tries to close it.</summary>
    public bool ConfirmClose()
    {
        if (!IsBusy) return true;
        if (Stage == WizardStage.Removing) return false;
        if (!Dialogs.Confirm("Scour is still working. Stop and close?")) return false;
        _waitCts?.Cancel();
        _scanCts?.Cancel();
        return true;
    }
}
