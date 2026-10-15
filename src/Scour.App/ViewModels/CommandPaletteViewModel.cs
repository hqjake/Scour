using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Search;

namespace Scour.App.ViewModels;

/// <summary>Ctrl+K: jump to any page, run common actions or uninstall a program by typing a few letters.</summary>
public sealed partial class CommandPaletteViewModel(MainViewModel main, AppServices services) : ObservableObject
{
    private List<CommandEntry> _entries = [];

    public ObservableCollection<CommandEntry> Results { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private CommandEntry? _selected;

    partial void OnQueryChanged(string value) => Search();

    [RelayCommand]
    public void Open()
    {
        _entries = BuildEntries();
        Query = "";
        Search();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[(i + delta + Results.Count) % Results.Count];
    }

    [RelayCommand]
    public void Run(CommandEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        IsOpen = false;
        try
        {
            switch (entry.Action)
            {
                case Func<Task> asyncAction: _ = asyncAction(); break;
                case Action action: action(); break;
            }
        }
        catch (Exception e)
        {
            Dialogs.Error(e.Message);
        }
    }

    private void Search()
    {
        Results.Clear();
        foreach (var e in CommandSearch.Find(_entries, Query, 9)) Results.Add(e);
        Selected = Results.FirstOrDefault();
    }

    // ---- what the palette can do

    private static readonly Dictionary<Type, string[]> PageKeywords = new()
    {
        [typeof(DashboardViewModel)] = ["home", "overview", "health", "score"],
        [typeof(ProgramsViewModel)] = ["uninstall", "remove", "programs", "apps", "revo", "installed"],
        [typeof(AppUpdatesViewModel)] = ["update", "upgrade", "winget", "outdated"],
        [typeof(BloatwareViewModel)] = ["bloat", "debloat", "preinstalled", "crapware"],
        [typeof(WindowsAppsViewModel)] = ["store", "appx", "uwp", "microsoft store"],
        [typeof(ReinstallKitViewModel)] = ["backup", "migrate", "new pc", "export", "import", "reinstall"],
        [typeof(TracesViewModel)] = ["trace", "install monitor", "logged"],
        [typeof(ForcedUninstallViewModel)] = ["force", "broken", "stubborn", "leftovers"],
        [typeof(BackupsViewModel)] = ["restore", "undo", "registry backup"],
        [typeof(JunkViewModel)] = ["clean", "temp", "cache", "junk", "free space", "ccleaner"],
        [typeof(BrowserPrivacyViewModel)] = ["browser", "history", "cookies", "chrome", "edge", "firefox"],
        [typeof(PrivacyViewModel)] = ["telemetry", "tracking", "ads", "privacy", "spying"],
        [typeof(FileFinderViewModel)] = ["duplicates", "large files", "big files", "copies"],
        [typeof(RegistryCleanerViewModel)] = ["registry", "regedit", "broken entries"],
        [typeof(SystemInfoViewModel)] = ["specs", "cpu", "gpu", "ram", "memory", "motherboard", "hardware", "temperature"],
        [typeof(RepairViewModel)] = ["fix", "sfc", "dism", "chkdsk", "network reset", "repair"],
        [typeof(AutorunViewModel)] = ["startup", "autorun", "boot", "sign in", "scheduled tasks"],
        [typeof(DiskAnalyzerViewModel)] = ["disk", "space", "wiztree", "treemap", "folder size", "storage"],
        [typeof(UsbViewModel)] = ["usb", "format", "bootable", "iso", "rufus", "flash drive"],
    };

    private List<CommandEntry> BuildEntries()
    {
        var list = new List<CommandEntry>();
        CommandEntry Entry(string title, string subtitle, string category, string glyph, string[] keywords, object action) =>
            new(title, subtitle, category, glyph, keywords, action);

        // Actions first: with an empty query the palette shows the most useful things.
        list.Add(Entry("Quick clean", "Remove safe junk now — temp files, caches, logs", "Action", "", ["clean", "junk", "free space", "temp"],
            () => main.QuickClean?.Invoke()));
        list.Add(Entry("Check PC health", "Run the health check on the dashboard", "Action", "", ["health", "score", "check"], (Func<Task>)(async () =>
        {
            main.Navigate(main.Dashboard);
            await main.Dashboard.CheckHealthCommand.ExecuteAsync(null);
        })));
        list.Add(Entry("Analyze disk space", "Scan the system drive and show what's using it", "Action", "", ["disk", "space", "wiztree", "scan"],
            RunOn<DiskAnalyzerViewModel>(p => p.ScanCommand.ExecuteAsync(null))));
        list.Add(Entry("Scan for junk", "Find temp files, caches and leftovers", "Action", "", ["junk", "scan", "clean"],
            RunOn<JunkViewModel>(p => p.ScanCommand.ExecuteAsync(null))));
        list.Add(Entry("Scan the registry", "Find entries pointing at missing files", "Action", "", ["registry", "scan"],
            RunOn<RegistryCleanerViewModel>(p => p.ScanCommand.ExecuteAsync(null))));
        list.Add(Entry("Check for app updates", "Find outdated programs with winget", "Action", "", ["update", "upgrade", "outdated"],
            RunOn<AppUpdatesViewModel>(p => p.RefreshCommand.ExecuteAsync(null))));
        list.Add(Entry("Apply recommended privacy protections", "Turn off telemetry, ads and tracking", "Action", "", ["privacy", "telemetry", "tracking"],
            RunOn<PrivacyViewModel>(p => { p.ApplyRecommendedCommand.Execute(null); return Task.CompletedTask; })));
        list.Add(Entry("Save a reinstall kit", "Save your app list for a new PC", "Action", "", ["backup", "export", "kit", "migrate"],
            RunOn<ReinstallKitViewModel>(p => p.SaveKitCommand.ExecuteAsync(null))));
        list.Add(Entry("Empty the Recycle Bin", "Permanently delete what's in it", "Action", "", ["recycle", "trash", "bin"], () =>
        {
            var (bytes, count) = RecycleBin.Query();
            if (count == 0) { Dialogs.Info("The Recycle Bin is already empty."); return; }
            if (Dialogs.Confirm($"Permanently delete {Format.Plural((int)count, "item")} ({Format.Bytes(bytes)}) from the Recycle Bin?") && RecycleBin.Empty())
                SoundService.Play(UiSound.Success);
        }));
        list.Add(Entry(ThemeService.IsDark ? "Switch to light mode" : "Switch to dark mode", "Change the theme", "Action",
            ThemeService.IsDark ? "" : "", ["theme", "dark", "light", "mode"], () => main.ToggleThemeCommand.Execute(null)));
        list.Add(Entry(SoundService.Enabled ? "Turn sounds off" : "Turn sounds on", "Interface sounds", "Action",
            SoundService.Enabled ? "" : "", ["sound", "mute", "audio"], () => main.ToggleSoundCommand.Execute(null)));
        list.Add(Entry("Check for Scour updates", "See if a newer Scour is available", "Action", "", ["scour", "version", "update", "upgrade"],
            (Func<Task>)(async () => Dialogs.Info(await main.CheckForUpdatesAsync(silent: false)))));

        // Pages.
        foreach (var page in main.Pages)
            list.Add(Entry(page.Title, $"Open · {page.Group}", "Page", page.Glyph,
                PageKeywords.GetValueOrDefault(page.GetType(), []), () => main.Navigate(page)));
        list.Add(Entry("Settings", "Open · Scour", "Page", main.Settings.Glyph, ["options", "preferences", "configure"], () => main.Navigate(main.Settings)));

        // Repairs.
        if (main.Pages.OfType<RepairViewModel>().FirstOrDefault() is { } repair)
            foreach (var item in repair.Actions)
                list.Add(Entry(item.Name, "Repair · " + item.Duration, "Repair", item.Glyph, ["repair", "fix"], () =>
                {
                    main.Navigate(repair);
                    repair.RunCommand.Execute(item);
                }));

        // Every installed program.
        foreach (var program in services.LastKnownPrograms)
            list.Add(Entry("Uninstall " + program.DisplayName,
                string.Join(" · ", new[] { program.Publisher, program.DisplayVersion }.Where(s => !string.IsNullOrWhiteSpace(s))),
                "Program", "", ["uninstall", "remove", program.Publisher ?? ""], () => main.UninstallProgram(program)));
        return list;
    }

    private Func<Task> RunOn<T>(Func<T, Task> action) where T : PageViewModel => async () =>
    {
        var page = main.Pages.OfType<T>().FirstOrDefault();
        if (page is null) return;
        main.Navigate(page);
        await Task.Delay(50); // first visit starts loading the page
        if (!page.IsLoading) await action(page);
    };
}
