using Scour.Core.Platform;

namespace Scour.Core.Programs;

public enum InstallScope
{
    Machine,
    User,
}

public sealed class InstalledProgram
{
    /// <summary>Uninstall registry key that describes this program.</summary>
    public required RegKeyPath RegistryKey { get; init; }
    public string KeyName => RegistryKey.Name;

    public required string DisplayName { get; init; }
    public string? DisplayVersion { get; init; }
    public string? Publisher { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>Install folder, either declared by the program or inferred from its icon/uninstaller path.</summary>
    public string? InstallLocation { get; init; }
    public bool InstallLocationInferred { get; init; }

    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? ModifyPath { get; init; }
    public string? DisplayIcon { get; init; }

    /// <summary>Size in bytes (from EstimatedSize, which is reported in KB).</summary>
    public long? EstimatedSizeBytes { get; init; }

    public bool IsMsi { get; init; }
    public string? ProductCode { get; init; }
    public bool Is32BitEntry { get; init; }
    public InstallScope Scope { get; init; }
    public bool IsSystemComponent { get; init; }
    public bool IsUpdate { get; init; }
    public bool NoRemove { get; init; }

    public string? HelpLink { get; init; }
    public string? UrlInfoAbout { get; init; }
    public string? Comments { get; init; }
    public string? Contact { get; init; }

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString) || ProductCode is not null;

    public string Architecture => Is32BitEntry ? "32-bit" : "64-bit";

    public override string ToString() => DisplayVersion is null ? DisplayName : $"{DisplayName} {DisplayVersion}";
}
