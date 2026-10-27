using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scour.Core.Leftovers;

namespace Scour.App.Services;

public sealed class AppSettings
{
    public ScanMode DefaultScanMode { get; set; } = ScanMode.Moderate;
    public bool CreateRestorePoint { get; set; } = true;
    public bool FilesToRecycleBin { get; set; } = true;
    public bool BackupRegistry { get; set; } = true;
    public bool ShowSystemComponents { get; set; }
    public bool ShowUpdates { get; set; }
    public bool QuickUninstallSilent { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;
    public List<string> IgnoredUpdates { get; set; } = [];
    public DateTime? LastCleanupUtc { get; set; }

    // Background
    public Scour.Core.Schedule.CleaningSchedule AutoClean { get; set; } = Scour.Core.Schedule.CleaningSchedule.Off;
    public DayOfWeek AutoCleanDay { get; set; } = DayOfWeek.Sunday;
    public int AutoCleanHour { get; set; } = 12;
    public bool AutoCleanRecycleBin { get; set; }
    public bool KeepInTray { get; set; } = true;
    public bool StartInTray { get; set; }
    public bool LowDiskAlerts { get; set; } = true;
    public DateTime? LastLowDiskAlertUtc { get; set; }
    public bool TrayHintShown { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Game mode boosts the user picked; null until they change the defaults.</summary>
    public List<string>? GameModeBoosts { get; set; }

    /// <summary>Switch Game mode on while an installed game runs, and off when it exits.</summary>
    public bool AutoGameMode { get; set; }

    /// <summary>Per-game choices, keyed by <see cref="Scour.Core.Gaming.GameInfo.Id"/>.</summary>
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = [];

    /// <summary>Folders the user added as games (anything a launcher doesn't report).</summary>
    public List<string> CustomGameFolders { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scour", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* settings are a convenience */ }
    }
}

public sealed class GameProfile
{
    /// <summary>False: this game never switches Game mode on by itself.</summary>
    public bool Auto { get; set; } = true;

    /// <summary>Boosts for this game; null uses the ones chosen on the Game mode page.</summary>
    public List<string>? Boosts { get; set; }
}
