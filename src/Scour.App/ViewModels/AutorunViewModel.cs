using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Autorun;

namespace Scour.App.ViewModels;

public sealed partial class AutorunEntryViewModel : ObservableObject
{
    private readonly AutorunManager _manager;
    private bool _suppress;

    public AutorunEntryViewModel(AutorunEntry entry, AutorunManager manager)
    {
        Entry = entry;
        _manager = manager;
        _isEnabled = entry.Enabled;
    }

    public AutorunEntry Entry { get; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Location => Entry.Location;
    public bool CanToggle => Entry.ApprovalKey is not null;
    public string Status => !Entry.TargetExists && Entry.TargetPath is not null ? "File not found" : "";

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress) return;
        try
        {
            _manager.SetEnabled(Entry, value, DateTime.UtcNow);
            ChangeHistory.Record("Startup", $"{(value ? "Enabled" : "Disabled")} startup entry {Name}", "Switch it back on the Startup programs page.");
        }
        catch (Exception e)
        {
            _suppress = true;
            IsEnabled = !value;
            _suppress = false;
            Dialogs.Error($"Could not change \"{Name}\": {e.Message}");
        }
    }
}

public sealed partial class AutorunViewModel(AppServices services) : PageViewModel
{
    public override string Title => "Startup programs";
    public override string Group => "System";
    public override string Glyph => "";

    public ObservableCollection<AutorunEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(OpenLocationCommand), nameof(SearchOnlineCommand))]
    private AutorunEntryViewModel? _selected;

    protected override Task LoadAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in services.Autorun.GetEntries()) Entries.Add(new AutorunEntryViewModel(e, services.Autorun));
        int disabled = Entries.Count(e => !e.IsEnabled);
        StatusText = $"{Format.Plural(Entries.Count, "startup entry", "startup entries")}" + (disabled > 0 ? $" · {disabled} disabled" : "");
    }

    private bool HasSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        if (Selected is not { } e) return;
        if (!Dialogs.Confirm($"Delete the startup entry \"{e.Name}\"?\n\nTo stop it temporarily, untick it instead.")) return;
        try
        {
            services.Autorun.Delete(e.Entry);
            ChangeHistory.Record("Startup", $"Deleted startup entry {e.Name}");
            Refresh();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenLocation()
    {
        if (Selected is not { } e) return;
        if (e.Entry.TargetPath is { } t) Shell.OpenFolder(t);
        else if (e.Entry.RunKey is { } k) Shell.OpenRegistryKey(k);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void SearchOnline()
    {
        if (Selected is { } e) Shell.SearchWeb($"\"{e.Name}\" startup");
    }
}
