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
