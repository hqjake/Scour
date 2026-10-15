using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Shell;

namespace Scour.App.ViewModels;

public sealed partial class MenuEntryViewModel(ContextMenuEntry entry, string program, Action<MenuEntryViewModel, bool> toggle) : ObservableObject
{
    public ContextMenuEntry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Kind => Entry.Kind == MenuEntryKind.Command ? "Command" : "Shell extension";
    public string AppliesTo => Entry.AppliesTo + (Entry.ShiftOnly ? " (Shift)" : "");
    public string Program { get; } = program;
    public string Target => Entry.Target ?? Entry.Clsid ?? "";
    public bool IsWindows => Entry.IsWindows;

    private bool _isEnabled = entry.IsEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            toggle(this, value);
        }
    }

    public void Refresh(ContextMenuEntry updated)
    {
        Entry = updated;
        _isEnabled = updated.IsEnabled;
        OnPropertyChanged(nameof(IsEnabled));
    }
}

/// <summary>Turn off clutter in Explorer's right-click menu.</summary>
public sealed partial class ContextMenuViewModel : PageViewModel
{
    private readonly ContextMenu _menu;
    private bool _explorerRestartNeeded;

    public ContextMenuViewModel(AppServices services)
    {
        _menu = new ContextMenu(services.Registry, services.Folders, NativeMethods.LoadIndirectString);
        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = o => o is MenuEntryViewModel e && (ShowWindows || !e.IsWindows) &&
                           (SearchText.Length == 0 || e.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
                            e.Program.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
    }

    public override string Title => "Context menu";
    public override string Glyph => "\uE8A7";
    public override string Group => "System";

    public ObservableCollection<MenuEntryViewModel> Entries { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private bool _showWindows;
    [ObservableProperty] private string _searchText = "";

    partial void OnShowWindowsChanged(bool value)
    {
        View.Refresh();
        UpdateStatus();
    }
    partial void OnSearchTextChanged(string value) => View.Refresh();

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusText = "Reading right-click menu entries…";
        try
        {
            var list = await Task.Run(() => _menu.Scan().Select(e => (Entry: e, Program: Describe(e))).ToList());
            Entries.Clear();
            foreach (var (e, program) in list) Entries.Add(new MenuEntryViewModel(e, program, Toggle));
            UpdateStatus();
        }
        catch (Exception e)
        {
            StatusText = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Toggle(MenuEntryViewModel item, bool enabled)
    {
        try
        {
            _menu.SetEnabled(item.Entry, enabled);
            // A shell extension is blocked by its ID, so every place it appears changes together.
            if (item.Entry.Kind == MenuEntryKind.Handler)
            {
                _explorerRestartNeeded = true;
                foreach (var other in Entries.Where(o => o != item && o.Entry.Clsid == item.Entry.Clsid))
                    other.Refresh(other.Entry with { IsEnabled = enabled });
            }
            UpdateStatus();
        }
        catch (Exception e)
        {
            Dialogs.Error("Couldn't change that entry: " + e.Message);
            item.Refresh(item.Entry);
        }
    }

    private void UpdateStatus()
    {
        int programs = Entries.Count(e => !e.IsWindows);
        int off = Entries.Count(e => !e.IsEnabled && (ShowWindows || !e.IsWindows));
        StatusText = $"{Format.Plural(programs, "entry", "entries")} added by programs" + (off > 0 ? $", {off} switched off" : "") + ". " +
                     (_explorerRestartNeeded ? "Restart Explorer to see shell extension changes." : "Switch an entry off to hide it; switch it back on any time.");
    }

    /// <summary>"C:\…\7-zip.dll" → "7-Zip" (from the file's version info).</summary>
    private static string Describe(ContextMenuEntry entry)
    {
        var target = entry.Target;
        if (string.IsNullOrWhiteSpace(target)) return "";
        var path = entry.Kind == MenuEntryKind.Handler || File.Exists(target) ? target : Scour.Core.Programs.CommandLine.Split(target).Executable;
        try
        {
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName : info.FileDescription;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch { /* unreadable file */ }
        return Path.GetFileName(path);
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { /* already gone */ }
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        _explorerRestartNeeded = false;
        UpdateStatus();
    }
}
