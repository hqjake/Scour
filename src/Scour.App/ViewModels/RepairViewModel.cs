using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Repair;

namespace Scour.App.ViewModels;

public sealed partial class RepairItemViewModel(RepairAction action) : ObservableObject
{
    public RepairAction Action { get; } = action;
    public string Name => Action.Name;
    public string Description => Action.Description;
    public string Glyph => Action.Glyph;
    public string Duration => Action.Duration + (Action.NeedsRestart ? " · restart needed" : "");

    [ObservableProperty] private string _state = "";
}

public sealed partial class RepairViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    public RepairViewModel(AppServices services)
    {
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        foreach (var a in RepairCatalog.Create(services.Folders, drive)) Actions.Add(new RepairItemViewModel(a));
        StatusText = "Each fix uses Windows' own repair tools. Pick one to run it.";
    }

    public override string Title => "Repair center";
    public override string Glyph => "\uE90F";
    public override string Group => "System";

    public ObservableCollection<RepairItemViewModel> Actions { get; } = [];
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty] private bool _restartNeeded;
    [ObservableProperty] private string _current = "";

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(RepairItemViewModel? item)
    {
        if (item is null) return;
        var a = item.Action;
        var question = $"Run \"{a.Name}\"?\n\n{a.Description}\n\nTakes about {a.Duration}." +
                       (a.Warning is null ? "" : "\n\n" + a.Warning) + (a.NeedsRestart ? "\n\nA restart is needed afterwards." : "");
        if (!(a.Warning is null ? Dialogs.Confirm(question) : Dialogs.ConfirmWarning(question))) return;

        IsRunning = IsLoading = true;
        Current = a.Name;
        item.State = "Running…";
        Output.Clear();
        _cts = new CancellationTokenSource();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        void Write(string line) => dispatcher.BeginInvoke(() =>
        {
            Output.Add(line);
            if (Output.Count > 2000) Output.RemoveAt(0);
        });
        try
        {
            var ok = await Task.Run(() => RepairRunner.RunAsync(a, Write, _cts.Token));
            item.State = ok ? "Done" : "Finished with problems";
            if (ok && a.NeedsRestart) RestartNeeded = true;
            StatusText = ok ? $"{a.Name}: finished." : $"{a.Name}: finished with problems — see the output.";
            ChangeHistory.Record("Repair", $"Ran \"{a.Name}\"", ok ? "Finished." : "Finished with problems.");
            SoundService.Play(ok ? UiSound.Success : UiSound.Warning);
        }
        catch (OperationCanceledException)
        {
            item.State = "Cancelled";
            StatusText = "Cancelled.";
        }
        finally
        {
            IsRunning = IsLoading = false;
            Current = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RestartNow()
    {
        if (Dialogs.Confirm("Restart the PC now? Save your work in other programs first."))
            Process.Start(new ProcessStartInfo(ProcessRunner.ToolPath("shutdown.exe"), "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
    }
}
