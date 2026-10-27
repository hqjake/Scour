using System.IO;
using Scour.App.Platform;
using Scour.Core.Autorun;
using Scour.Core.Junk;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Removal;
using Scour.Core.Tracing;

namespace Scour.App.Services;

/// <summary>Composition root: one instance of every engine service for the app's lifetime.</summary>
public sealed class AppServices
{
    public IRegistry Registry { get; }
    public IFileSystem FileSystem { get; }
    public KnownFolders Folders { get; }
    public AppSettings Settings { get; }
    public ProgramInventory Inventory { get; }
    public LeftoverScanner Scanner { get; }
    public BackupStore Backups { get; }
    public TraceStore Traces { get; }
    public AutorunManager Autorun { get; }
    public JunkCleaner Junk { get; }
    public IconCache Icons { get; }
    public IServiceController ServiceController { get; }
    public GameModeService GameMode { get; }
    public OverlayService Overlay { get; }

    /// <summary>Apps with updates at the last check (null until App updates has run).</summary>
    public int? LastUpdatesCount { get; set; }

    /// <summary>Most recent program list, used by forced uninstall, hunter mode and safety checks.</summary>
    public IReadOnlyList<InstalledProgram> LastKnownPrograms { get; set; } = [];

    public AppServices()
    {
        Registry = new WindowsRegistry();
        FileSystem = new WindowsFileSystem();
        Folders = WindowsFolders.Create();
        Settings = AppSettings.Load();
        Inventory = new ProgramInventory(Registry, Folders);
        Scanner = new LeftoverScanner(Registry, FileSystem, Folders);
        Backups = new BackupStore(FileSystem, Path.Combine(Folders.AppDataDir, "Backups"));
        Traces = new TraceStore(FileSystem, Path.Combine(Folders.AppDataDir, "Traces"));
        Autorun = new AutorunManager(Registry, FileSystem, Folders);
        Junk = new JunkCleaner(FileSystem, Folders);
        Icons = new IconCache(Folders);
        ServiceController = new WindowsServiceController();
        GameMode = GameModeService.Create(Folders.AppDataDir, Registry, FileSystem);
        ChangeHistory.Initialize(FileSystem, Folders.AppDataDir);
        Overlay = new OverlayService { Corner = Settings.OverlayCorner };
    }

    /// <summary>Raised when something outside the programs page installed or removed software.</summary>
    public event Action? ProgramsChanged;

    public void NotifyProgramsChanged() => ProgramsChanged?.Invoke();

    public LeftoverRemover CreateRemover() => new(Registry, FileSystem, Backups, ServiceController);

    public RemovalOptions RemovalOptions => new()
    {
        FilesToRecycleBin = Settings.FilesToRecycleBin,
        BackupRegistry = Settings.BackupRegistry,
    };

    /// <summary>Scour's own folder must never be swept up as a leftover.</summary>
    public IEnumerable<string> SelfProtectedPaths()
    {
        yield return AppContext.BaseDirectory.TrimEnd('\\');
        yield return Folders.AppDataDir;
    }

    public List<InstalledProgram> LoadPrograms()
    {
        var list = Inventory.GetPrograms(new InventoryOptions
        {
            IncludeSystemComponents = Settings.ShowSystemComponents,
            IncludeUpdates = Settings.ShowUpdates,
        });
        // Safety checks need every entry, visible or not.
        LastKnownPrograms = Inventory.GetPrograms(new InventoryOptions { IncludeSystemComponents = true, IncludeUpdates = true });
        return list;
    }
}
