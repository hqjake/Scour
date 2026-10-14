using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Removal;

namespace Scour.App.ViewModels;

public sealed partial class BackupsViewModel(AppServices services) : PageViewModel
{
    public override string Title => "Backups";
    public override string Glyph => "";

    public ObservableCollection<BackupManifest> Backups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DeleteCommand), nameof(OpenRegFileCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedItems))]
    private BackupManifest? _selected;

    public IReadOnlyList<LeftoverItemViewModel> SelectedItems =>
        Selected?.Items.Select(i => new LeftoverItemViewModel(i)).ToList() ?? [];

    protected override Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        Backups.Clear();
        foreach (var b in services.Backups.List()) Backups.Add(b);
        StatusText = Backups.Count == 0
            ? "No backups yet. Scour backs up registry entries every time it removes leftovers."
            : Format.Plural(Backups.Count, "backup");
    }

    private bool CanRestore() => Selected?.RegistryFile is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        if (Selected is not { } b) return;
        if (!Dialogs.Confirm($"Restore the registry entries removed for \"{b.ProgramName}\" on {b.CreatedUtc.ToLocalTime():g}?" +
                             (b.FilesSentToRecycleBin ? "\n\nDeleted files can be restored from the Recycle Bin." : "")))
            return;
        try
        {
            int n = services.Backups.RestoreRegistry(b, services.Registry);
            SoundService.Play(UiSound.Success);
            Dialogs.Info($"Restored {Format.Plural(n, "registry entry", "registry entries")}.");
            services.NotifyProgramsChanged();
        }
        catch (Exception e)
        {
            Dialogs.Error("Restore failed: " + e.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void OpenRegFile()
    {
        if (Selected is { } b) Shell.OpenFolder(services.Backups.GetRegistryFilePath(b));
    }

    [RelayCommand]
    private void OpenRecycleBin() => Shell.OpenRecycleBin();

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected is not { } b) return;
        if (!Dialogs.Confirm($"Delete the backup for \"{b.ProgramName}\"? You won't be able to restore its registry entries.")) return;
        try { services.Backups.Delete(b); }
        catch (Exception e) { Dialogs.Error(e.Message); }
        Refresh();
    }
}
