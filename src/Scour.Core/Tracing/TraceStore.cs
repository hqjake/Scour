using System.Text.Json;
using Scour.Core.Leftovers;
using Scour.Core.Platform;
using Scour.Core.Removal;

namespace Scour.Core.Tracing;

/// <summary>A recorded installation: everything the installer created.</summary>
public sealed class TraceLog
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DateTime CreatedUtc { get; init; }
    public string? InstallerPath { get; init; }
    public RegKeyPath? UninstallKey { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public int ModifiedValueCount { get; init; }
    public List<LeftoverItem> Items { get; init; } = [];

    public long TotalBytes => Items.Sum(i => i.SizeBytes);
}

public sealed class TraceStore(IFileSystem fs, string rootDir)
{
    public string RootDir => rootDir;

    public void Save(TraceLog log)
    {
        fs.CreateDirectory(rootDir);
        fs.WriteAllText(PathFor(log.Id), JsonSerializer.Serialize(log, BackupStore.JsonOptions));
    }

    public List<TraceLog> List()
    {
        var result = new List<TraceLog>();
        if (!fs.DirectoryExists(rootDir)) return result;
        foreach (var file in fs.EnumerateFiles(rootDir))
        {
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var log = JsonSerializer.Deserialize<TraceLog>(fs.ReadAllText(file), BackupStore.JsonOptions);
                if (log is not null) result.Add(log);
            }
            catch (JsonException) { /* skip corrupt logs */ }
        }
        return result.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Delete(TraceLog log)
    {
        var p = PathFor(log.Id);
        if (fs.FileExists(p)) fs.DeleteFile(p, toRecycleBin: false);
    }

    private string PathFor(string id) => WinPath.Combine(rootDir, id + ".json");
}
