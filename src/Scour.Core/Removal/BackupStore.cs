using System.Text.Json;
using System.Text.Json.Serialization;
using Scour.Core.Leftovers;
using Scour.Core.Platform;

namespace Scour.Core.Removal;

public sealed class BackupManifest
{
    public required string Id { get; init; }
    public required string ProgramName { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? RegistryFile { get; init; }
    public List<LeftoverItem> Items { get; init; } = [];
    public bool FilesSentToRecycleBin { get; init; }
}

/// <summary>
/// Keeps a registry backup (.reg) of everything Scour deletes, so removals can be undone from
/// the Backups page. Files go to the Recycle Bin rather than being copied.
/// </summary>
public sealed class BackupStore(IFileSystem fs, string rootDir)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string RootDir => rootDir;

    public BackupManifest Create(string programName, IReadOnlyList<LeftoverItem> items, IRegistry registry, bool filesToRecycleBin, DateTime nowUtc)
    {
        var id = nowUtc.ToString("yyyyMMdd-HHmmss") + "-" + Sanitize(programName);
        var dir = WinPath.Combine(rootDir, id);
        fs.CreateDirectory(dir);

        string? regFile = null;
        var regItems = items.Where(i => i.IsRegistry && i.RegistryKey is not null).ToList();
        if (regItems.Count > 0)
        {
            var writer = new RegFileWriter(registry);
            foreach (var i in regItems)
            {
                if (i.Kind == LeftoverKind.RegistryKey) writer.AddKeyTree(i.RegistryKey!.Value);
                else writer.AddValue(i.RegistryKey!.Value, i.ValueName ?? "");
            }
            regFile = "registry.reg";
            fs.WriteAllBytes(WinPath.Combine(dir, regFile), writer.ToBytes());
        }

        var manifest = new BackupManifest
        {
            Id = id,
            ProgramName = programName,
            CreatedUtc = nowUtc,
            RegistryFile = regFile,
            Items = items.ToList(),
            FilesSentToRecycleBin = filesToRecycleBin,
        };
        fs.WriteAllText(WinPath.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    public List<BackupManifest> List()
    {
        var result = new List<BackupManifest>();
        if (!fs.DirectoryExists(rootDir)) return result;
        foreach (var dir in fs.EnumerateDirectories(rootDir))
        {
            var file = WinPath.Combine(dir, "manifest.json");
            if (!fs.FileExists(file)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<BackupManifest>(fs.ReadAllText(file), JsonOptions);
                if (m is not null) result.Add(m);
            }
            catch (JsonException) { /* skip corrupt manifests */ }
        }
        return result.OrderByDescending(m => m.CreatedUtc).ToList();
    }

    /// <summary>Re-imports the registry part of a backup. Returns the number of operations applied.</summary>
    public int RestoreRegistry(BackupManifest manifest, IRegistry registry)
    {
        if (manifest.RegistryFile is null) return 0;
        var path = WinPath.Combine(rootDir, manifest.Id, manifest.RegistryFile);
        var entries = RegFileReader.Parse(RegFileReader.Decode(fs.ReadAllBytes(path)));
        return RegFileReader.Apply(registry, entries);
    }

    public string GetRegistryFilePath(BackupManifest manifest) =>
        manifest.RegistryFile is null ? "" : WinPath.Combine(rootDir, manifest.Id, manifest.RegistryFile);

    public void Delete(BackupManifest manifest) => fs.DeleteDirectory(WinPath.Combine(rootDir, manifest.Id), toRecycleBin: false);

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').Take(40).ToArray();
        return new string(chars).Trim().Replace(' ', '_');
    }
}
