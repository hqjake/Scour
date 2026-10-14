using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.Leftovers;

namespace Scour.App.ViewModels;

public sealed partial class SettingsViewModel(AppServices services) : PageViewModel
{
    private AppSettings S => services.Settings;

    public override string Title => "Settings";
    public override string Glyph => "";

    public IReadOnlyList<ScanMode> ScanModes { get; } = [ScanMode.Safe, ScanMode.Moderate, ScanMode.Advanced];

    public ScanMode DefaultScanMode
    {
        get => S.DefaultScanMode;
        set { S.DefaultScanMode = value; Save(); }
    }

    public bool CreateRestorePoint
    {
        get => S.CreateRestorePoint;
        set { S.CreateRestorePoint = value; Save(); }
    }

    public bool FilesToRecycleBin
    {
        get => S.FilesToRecycleBin;
        set { S.FilesToRecycleBin = value; Save(); }
    }

    public bool BackupRegistry
    {
        get => S.BackupRegistry;
        set { S.BackupRegistry = value; Save(); }
    }

    public bool QuickUninstallSilent
    {
        get => S.QuickUninstallSilent;
        set { S.QuickUninstallSilent = value; Save(); }
    }

    public bool ShowSystemComponents
    {
        get => S.ShowSystemComponents;
        set { S.ShowSystemComponents = value; Save(); services.NotifyProgramsChanged(); }
    }

    public bool ShowUpdates
    {
        get => S.ShowUpdates;
        set { S.ShowUpdates = value; Save(); services.NotifyProgramsChanged(); }
    }

    public string DataFolder => services.Folders.AppDataDir;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    private void Save()
    {
        S.Save();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        System.IO.Directory.CreateDirectory(DataFolder);
        Shell.OpenFolder(DataFolder);
    }
}
