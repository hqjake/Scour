using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.Core.Leftovers;

/// <summary>Everything the leftover scanner knows about the program being removed.</summary>
public sealed class ScanTarget
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public bool InstallLocationInferred { get; init; }
    public RegKeyPath? UninstallKey { get; init; }
    public string? ProductCode { get; init; }

    /// <summary>Executable file names found in the install folder before uninstalling (e.g. "firefox.exe").</summary>
    public HashSet<string> ExecutableNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Additional names supplied by the user (forced uninstall).</summary>
    public List<string> ExtraNames { get; init; } = [];

    /// <summary>Install folders of other installed programs; never deleted or contained in a deletion.</summary>
    public List<string> ProtectedPaths { get; init; } = [];

    /// <summary>Uninstall keys of other installed programs.</summary>
    public List<RegKeyPath> ProtectedKeys { get; init; } = [];
}

public static class ScanTargetBuilder
{
    /// <summary>
    /// Builds a scan target for <paramref name="program"/>. Call this <em>before</em> running the
    /// program's uninstaller so the executable names in its folder are still discoverable.
    /// </summary>
    public static ScanTarget FromProgram(InstalledProgram program, IEnumerable<InstalledProgram> allPrograms, IFileSystem fs,
                                         IEnumerable<string>? alsoProtect = null)
    {
        var others = allPrograms.Where(p => !p.RegistryKey.Equals(program.RegistryKey)).ToList();
        return new ScanTarget
        {
            DisplayName = program.DisplayName,
            Publisher = program.Publisher,
            InstallLocation = program.InstallLocation,
            InstallLocationInferred = program.InstallLocationInferred,
            UninstallKey = program.RegistryKey,
            ProductCode = program.ProductCode,
            ExecutableNames = program.InstallLocation is null ? new(StringComparer.OrdinalIgnoreCase) : CollectExecutables(fs, program.InstallLocation),
            ProtectedPaths = others.Select(o => o.InstallLocation).OfType<string>()
                                   .Concat(alsoProtect ?? []).Distinct(WinPath.Comparer).ToList(),
            ProtectedKeys = others.Select(o => o.RegistryKey).ToList(),
        };
    }

    /// <summary>Builds a target for a program that is no longer (or never was) in the installed list.</summary>
    public static ScanTarget Forced(string name, string? publisher, string? installFolder, IEnumerable<InstalledProgram> allPrograms,
                                    IFileSystem fs, IEnumerable<string>? alsoProtect = null)
    {
        var folder = string.IsNullOrWhiteSpace(installFolder) ? null : WinPath.Normalize(installFolder);
        return new ScanTarget
        {
            DisplayName = name,
            Publisher = publisher,
            InstallLocation = folder,
            ExecutableNames = folder is null ? new(StringComparer.OrdinalIgnoreCase) : CollectExecutables(fs, folder),
            ProtectedPaths = allPrograms.Select(o => o.InstallLocation).OfType<string>()
                                        .Where(p => folder is null || !WinPath.AreSame(p, folder))
                                        .Concat(alsoProtect ?? []).Distinct(WinPath.Comparer).ToList(),
            ProtectedKeys = allPrograms.Select(o => o.RegistryKey).ToList(),
        };
    }

    public static HashSet<string> CollectExecutables(IFileSystem fs, string folder, int maxDepth = 3, int max = 300)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!fs.DirectoryExists(folder)) return result;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((folder, 0));
        while (queue.Count > 0 && result.Count < max)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var f in fs.EnumerateFiles(dir))
                if (WinPath.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    result.Add(WinPath.GetFileName(f));
            if (depth < maxDepth)
                foreach (var d in fs.EnumerateDirectories(dir)) queue.Enqueue((d, depth + 1));
        }
        // Generic uninstaller names would match every other program's leftovers.
        result.RemoveWhere(IsGenericExecutable);
        return result;
    }

    private static bool IsGenericExecutable(string name)
    {
        var n = WinPath.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return n.StartsWith("unins") || n is "uninstall" or "uninst" or "setup" or "install" or "update" or "updater"
            or "helper" or "crashreporter" or "crashpad_handler" or "vcredist_x64" or "vcredist_x86" or "maintenancetool"
            or "elevate" or "launcher" or "notification_helper" or "dxsetup";
    }
}
