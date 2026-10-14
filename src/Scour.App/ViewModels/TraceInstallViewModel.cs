using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Tracing;

namespace Scour.App.ViewModels;

public enum TraceStage
{
    Choose,
    SnapshotBefore,
    Installing,
    SnapshotAfter,
    Review,
}

/// <summary>Records an installation by diffing system snapshots taken before and after it.</summary>
public sealed partial class TraceInstallViewModel(AppServices services) : ObservableObject
{
    private Snapshot? _before;
    private TraceResult? _result;
    private DateTime _startedUtc;

    public event Action? CloseRequested;
    public TraceLog? SavedLog { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _installerPath = "";

    [ObservableProperty] private string _arguments = "";
    [ObservableProperty] private TraceStage _stage = TraceStage.Choose;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _installerExited;

    public ObservableCollection<LeftoverItemViewModel> Items { get; } = [];

    public bool IsBusy => Stage is TraceStage.SnapshotBefore or TraceStage.SnapshotAfter;

    partial void OnStageChanged(TraceStage value) => OnPropertyChanged(nameof(IsBusy));

    [RelayCommand]
    private void Browse()
    {
        var file = Dialogs.PickFile("Choose an installer", "Installers (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*");
        if (file is not null) InstallerPath = file;
    }

    private bool CanStart() => File.Exists(InstallerPath);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Stage = TraceStage.SnapshotBefore;
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var taker = new SnapshotTaker(services.Registry, services.FileSystem, services.Folders);
            _before = await Task.Run(() => taker.Take(progress));
            _startedUtc = DateTime.UtcNow;

            var psi = InstallerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{InstallerPath}\" {Arguments}".Trim())
                : new ProcessStartInfo(InstallerPath, Arguments);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(InstallerPath) ?? "";
            var process = Process.Start(psi);
            Stage = TraceStage.Installing;
            Status = "Complete the installation, then click \"Installation finished\".";
            if (process is not null)
            {
                await process.WaitForExitAsync();
                InstallerExited = true;
                Status = "The installer has closed. If the installation is complete, click \"Installation finished\".";
            }
        }
        catch (Exception e)
        {
            Dialogs.Error("Could not start tracing: " + e.Message);
            Stage = TraceStage.Choose;
        }
    }

    [RelayCommand]
    private async Task FinishInstallAsync()
    {
        if (_before is null) return;
        Stage = TraceStage.SnapshotAfter;
        var progress = new Progress<string>(s => Status = s);
        try
        {
            var taker = new SnapshotTaker(services.Registry, services.FileSystem, services.Folders);
            var after = await Task.Run(() => taker.Take(progress));
            var ignored = services.SelfProtectedPaths().Append(services.Folders.UserTemp).ToList();
            _result = await Task.Run(() =>
            {
                var r = SnapshotDiff.Compare(_before, after, ignored);
                foreach (var i in r.Items)
                {
                    try
                    {
                        if (i.Kind == LeftoverKind.Folder) i.SizeBytes = services.FileSystem.GetDirectorySize(i.Path);
                        else if (i.Kind == LeftoverKind.File) i.SizeBytes = services.FileSystem.GetFileSize(i.Path);
                    }
                    catch { /* informational */ }
                }
                return r;
            });

            Items.Clear();
            foreach (var i in _result.Items) Items.Add(new LeftoverItemViewModel(i) { IsChecked = true });

            var uninstallKey = _result.NewUninstallKeys.FirstOrDefault();
            var program = uninstallKey is { } k ? services.Inventory.TryRead(k) : null;
            Name = program?.DisplayName ?? Path.GetFileNameWithoutExtension(InstallerPath);
            Summary = $"The installer created {Format.Plural(_result.Items.Count, "item")}" +
                      (_result.ModifiedValueCount > 0 ? $" and changed {Format.Plural(_result.ModifiedValueCount, "existing registry value")}" : "") +
                      ". Untick anything that wasn't created by this program (for example files other apps wrote meanwhile).";
            Stage = TraceStage.Review;
            SoundService.Play(UiSound.Scan);
        }
        catch (Exception e)
        {
            Dialogs.Error("Could not finish tracing: " + e.Message);
            Stage = TraceStage.Installing;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (_result is null) return;
        var uninstallKey = _result.NewUninstallKeys.FirstOrDefault();
        var program = uninstallKey is { } k ? services.Inventory.TryRead(k) : null;
        var log = new TraceLog
        {
            Id = _startedUtc.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(InstallerPath) : Name.Trim(),
            CreatedUtc = _startedUtc,
            InstallerPath = InstallerPath,
            UninstallKey = uninstallKey,
            Publisher = program?.Publisher,
            Version = program?.DisplayVersion,
            ModifiedValueCount = _result.ModifiedValueCount,
            Items = Items.Where(i => i.IsChecked).Select(i => i.Item).ToList(),
        };
        services.Traces.Save(log);
        SavedLog = log;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Stage is TraceStage.Installing or TraceStage.Review &&
            !Dialogs.Confirm("Discard this trace? The installed program stays installed, but Scour won't have a log of it."))
            return;
        CloseRequested?.Invoke();
    }

    public bool ConfirmClose() => !IsBusy && (Stage is TraceStage.Choose || SavedLog is not null ||
        Dialogs.Confirm("Discard this trace? The installed program stays installed, but Scour won't have a log of it."));
}
