using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Services;
using Scour.Core.Privacy;
using Scour.Core.Removal;
using Scour.Core.Updates;

namespace Scour.App.ViewModels;

public sealed record HistoryRow(ActivityEntry Entry)
{
    public string Time => Entry.TimeUtc.ToLocalTime().ToString("g");
    public string Day => Entry.TimeUtc.ToLocalTime().Date == DateTime.Today ? "Today"
                       : Entry.TimeUtc.ToLocalTime().Date == DateTime.Today.AddDays(-1) ? "Yesterday"
                       : Entry.TimeUtc.ToLocalTime().ToString("D");
    public string Title => Entry.Title;
    public string Category => Entry.Category;
    public string? Detail => Entry.Detail;
    public bool CanUndo => Entry.CanUndo;
    public bool Undone => Entry.Undone;
}

/// <summary>Everything Scour has changed, with undo for the changes that can be reversed.</summary>
public sealed partial class ChangeHistoryViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly GameModeViewModel _gameMode;

    public ChangeHistoryViewModel(AppServices services, GameModeViewModel gameMode)
    {
        _s = services;
        _gameMode = gameMode;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryRow.Day)));
    }

    public override string Title => "Change history";
    public override string Glyph => "";
    public override string Group => "Programs";

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private HistoryRow? _selected;

    protected override Task LoadAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    public override void OnShown()
    {
        if (Rows.Count > 0 || StatusText.Length > 0) Reload(); // pick up what other pages did since
    }

    [RelayCommand]
    private void Reload()
    {
        var entries = ChangeHistory.List();
        Rows.Clear();
        foreach (var e in entries) Rows.Add(new HistoryRow(e));
        StatusText = entries.Count == 0 ? "Nothing changed yet. Everything Scour changes from now on is listed here." :
            $"{Format.Plural(entries.Count, "change")}. {entries.Count(e => e.CanUndo)} can be undone here; files deleted to the Recycle Bin can be restored from there.";
    }

    private bool CanUndo() => Selected?.CanUndo == true;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (Selected is not { Entry: { Undo: { } undo } entry }) return;
        if (!Dialogs.Confirm($"Undo \"{entry.Title}\"?")) return;
        try
        {
            var result = await RunUndo(undo);
            ChangeHistory.MarkUndone(entry.Id);
            ChangeHistory.Record(entry.Category, "Undid: " + entry.Title, result);
            StatusText = result;
            SoundService.Play(UiSound.Success);
        }
        catch (Exception e)
        {
            StatusText = "Couldn't undo it: " + e.Message;
            SoundService.Play(UiSound.Warning);
        }
        Reload();
    }

    private async Task<string> RunUndo(UndoInfo undo)
    {
        switch (undo.Kind)
        {
            case ChangeHistory.RegistryBackup:
            {
                var backup = _s.Backups.List().FirstOrDefault(b => b.Id == undo.Data)
                             ?? throw new InvalidOperationException("Its backup was deleted from the Backups page.");
                int n = await Task.Run(() => _s.Backups.RestoreRegistry(backup, _s.Registry));
                _s.NotifyProgramsChanged();
                return $"Restored {Format.Plural(n, "registry entry", "registry entries")}." +
                       (backup.FilesSentToRecycleBin ? " Deleted files are in the Recycle Bin if you need them back." : "");
            }
            case ChangeHistory.PrivacyTweak:
            {
                var parts = undo.Data.Split(':', 2);
                var tweak = PrivacyTweaks.All.FirstOrDefault(t => t.Id == parts[^1]) ?? throw new InvalidOperationException("That setting no longer exists.");
                var engine = new PrivacyTweaks(_s.Registry, _s.FileSystem, Path.Combine(_s.Folders.AppDataDir, "privacy-backup.json"));
                if (parts[0] == "on") engine.Apply(tweak);
                else engine.Undo(tweak);
                return $"\"{tweak.Name}\" is {(parts[0] == "on" ? "on" : "off")} again.";
            }
            case ChangeHistory.GameModeOn:
                if (!_gameMode.IsOn) return "Game mode was already off.";
                await _gameMode.ToggleCommand.ExecuteAsync(null);
                return "Game mode is off; its settings were put back.";
            case ChangeHistory.UpdatePause:
                new WindowsUpdatePause(_s.Registry).Resume();
                return "Windows Update runs normally again.";
            case ChangeHistory.DeviceAccess:
            {
                var parts = undo.Data.Split('|');
                if (parts.Length != 3 || !Enum.TryParse<Capability>(parts[0], out var cap)) throw new InvalidOperationException("Unknown setting.");
                bool allow = parts[2] == "allow";
                var access = new DeviceAccess(_s.Registry);
                if (parts[1] == "all") access.SetAllowed(cap, allow);
                else if (parts[1] == "desktop") access.SetDesktopAppsAllowed(cap, allow);
                else if (parts[1].StartsWith("app:", StringComparison.Ordinal)) access.SetAppAllowed(cap, parts[1][4..], allow);
                return $"{cap} access is {(allow ? "allowed" : "blocked")} again.";
            }
            default:
                throw new InvalidOperationException("This change can't be undone here.");
        }
    }
}
