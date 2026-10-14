using System.Text.Json.Serialization;
using Scour.Core.Platform;

namespace Scour.Core.Leftovers;

public enum LeftoverKind
{
    RegistryKey,
    RegistryValue,
    Folder,
    File,
}

/// <summary>How sure the scanner is that the item belongs to the removed program.</summary>
public enum Confidence
{
    /// <summary>Directly tied to the program (its own key, folder, or a reference into it). Pre-selected.</summary>
    Safe = 0,
    /// <summary>Strong name match. Review before deleting.</summary>
    Moderate = 1,
    /// <summary>Loose match. Usually wrong; shown only in Advanced mode.</summary>
    Risky = 2,
}

public enum ScanMode
{
    Safe,
    Moderate,
    Advanced,
}

public sealed class LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>File/folder path, or for registry items the key path ("HKLM\SOFTWARE\...").</summary>
    public required string Path { get; init; }

    public RegKeyPath? RegistryKey { get; init; }

    /// <summary>For <see cref="LeftoverKind.RegistryValue"/>: the value's name ("" = default value).</summary>
    public string? ValueName { get; init; }

    /// <summary>Optional value data, for display.</summary>
    public string? ValueData { get; init; }

    public Confidence Confidence { get; set; }
    public List<string> Reasons { get; init; } = [];
    public long SizeBytes { get; set; }

    /// <summary>Set when the item is a Windows service key; the service is stopped/deleted before the key goes.</summary>
    public string? ServiceName { get; init; }

    [JsonIgnore]
    public bool IsRegistry => Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue;

    [JsonIgnore]
    public string Identity => Kind switch
    {
        LeftoverKind.RegistryValue => $"V|{Path.ToUpperInvariant()}|{ValueName?.ToUpperInvariant()}",
        LeftoverKind.RegistryKey => $"K|{Path.ToUpperInvariant()}",
        _ => $"F|{WinPath.Normalize(Path).ToUpperInvariant()}",
    };

    [JsonIgnore]
    public string DisplayText => Kind == LeftoverKind.RegistryValue
        ? $"{Path}  →  {(string.IsNullOrEmpty(ValueName) ? "(Default)" : ValueName)}"
        : Path;

    public static LeftoverItem ForKey(RegKeyPath key, Confidence c, string reason, string? serviceName = null) => new()
    {
        Kind = LeftoverKind.RegistryKey,
        Path = key.ToString(),
        RegistryKey = key,
        Confidence = c,
        Reasons = [reason],
        ServiceName = serviceName,
    };

    public static LeftoverItem ForValue(RegKeyPath key, string valueName, string? data, Confidence c, string reason) => new()
    {
        Kind = LeftoverKind.RegistryValue,
        Path = key.ToString(),
        RegistryKey = key,
        ValueName = valueName,
        ValueData = data,
        Confidence = c,
        Reasons = [reason],
    };

    public static LeftoverItem ForFolder(string path, Confidence c, string reason) => new()
    {
        Kind = LeftoverKind.Folder,
        Path = WinPath.Normalize(path),
        Confidence = c,
        Reasons = [reason],
    };

    public static LeftoverItem ForFile(string path, Confidence c, string reason) => new()
    {
        Kind = LeftoverKind.File,
        Path = WinPath.Normalize(path),
        Confidence = c,
        Reasons = [reason],
    };

    public override string ToString() => $"[{Confidence}] {DisplayText}";
}
