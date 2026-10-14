using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Tracing;

namespace Scour.App.ViewModels;

public sealed partial class TracesViewModel(AppServices services) : PageViewModel
{
    public override string Title => "Traced programs";
    public override string Glyph => "";

    public ObservableCollection<TraceLog> Traces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand), nameof(DeleteCommand), nameof(SaveNameCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedItems))]
    private TraceLog? _selected;

    [ObservableProperty] private string _editName = "";

    public IReadOnlyList<LeftoverItemViewModel> SelectedItems =>
        Selected?.Items.Select(i => new LeftoverItemViewModel(i)).ToList() ?? [];

    partial void OnSelectedChanged(TraceLog? value) => EditName = value?.Name ?? "";

    protected override Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        var id = Selected?.Id;
        Traces.Clear();
        foreach (var t in services.Traces.List()) Traces.Add(t);
        Selected = Traces.FirstOrDefault(t => t.Id == id);
        StatusText = Traces.Count == 0
            ? "No traced programs yet. Use \"Install and trace\" to record everything an installer does."
            : Format.Plural(Traces.Count, "traced program");
    }

    [RelayCommand]
    private void InstallAndTrace()
    {
        var vm = new TraceInstallViewModel(services);
        Dialogs.ShowTraceInstall(vm);
        Refresh();
        if (vm.SavedLog is { } log) Selected = Traces.FirstOrDefault(t => t.Id == log.Id);
    }

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Uninstall()
    {
        if (Selected is not { } trace) return;
        var program = trace.UninstallKey is { } key ? services.Inventory.TryRead(key) : null;
        bool runUninstaller = program is not null &&
            Dialogs.Confirm($"\"{program.DisplayName}\" is still installed.\n\nRun its own uninstaller first? (Recommended)\n\n" +
                            "Choose No to remove the traced files and registry entries directly.");
        var job = new UninstallJob
        {
            Title = trace.Name,
            Program = runUninstaller ? program : null,
            RunUninstaller = runUninstaller,
            Trace = trace,
        };
        Dialogs.ShowUninstallWizard(new UninstallWizardViewModel(services, [job], quick: false));
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected is not { } trace) return;
        if (!Dialogs.Confirm($"Delete the trace log for \"{trace.Name}\"?\n\nThe program itself is not touched.")) return;
        services.Traces.Delete(trace);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void SaveName()
    {
        if (Selected is not { } trace || string.IsNullOrWhiteSpace(EditName)) return;
        trace.Name = EditName.Trim();
        services.Traces.Save(trace);
        Refresh();
    }
}
