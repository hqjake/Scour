using Scour.Core.Platform;

namespace Scour.Core.Junk;

/// <summary>A place where disposable files accumulate.</summary>
public sealed record JunkLocation(string Pattern, string FileFilter = "*", bool Recursive = true);

public sealed class JunkCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required List<JunkLocation> Locations { get; init; }

    /// <summary>Files newer than this are left alone (they may still be in use).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

    public bool EnabledByDefault { get; init; } = true;
}

public sealed class JunkScanResult
{
    public required JunkCategory Category { get; init; }
    public List<string> Files { get; } = [];
    public long TotalBytes { get; set; }
    public List<string> Roots { get; } = [];
}

public sealed class JunkCleanResult
{
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public int FilesSkipped { get; set; }
}

/// <summary>Finds and deletes temporary files, caches, crash dumps and similar junk.</summary>
public sealed class JunkCleaner(IFileSystem fs, KnownFolders folders)
{
    public List<JunkCategory> Categories { get; } = DefaultCategories(folders);

    public static List<JunkCategory> DefaultCategories(KnownFolders f) =>
    [
        new()
        {
            Id = "user-temp", Name = "User temporary files",
            Description = "Files programs left in your Temp folder. Files from the last 24 hours are kept.",
            Locations = [new(f.UserTemp)], MinAge = TimeSpan.FromHours(24),
        },
        new()
        {
            Id = "windows-temp", Name = "Windows temporary files",
            Description = "Files in the Windows Temp folder older than 24 hours.",
            Locations = [new(WinPath.Combine(f.WindowsDir, "Temp"))], MinAge = TimeSpan.FromHours(24),
        },
        new()
        {
            Id = "wer", Name = "Error reports",
            Description = "Windows Error Reporting archives and queued crash reports.",
            Locations =
            [
                new(WinPath.Combine(f.ProgramData, @"Microsoft\Windows\WER\ReportArchive")),
                new(WinPath.Combine(f.ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                new(WinPath.Combine(f.LocalAppData, @"Microsoft\Windows\WER\ReportArchive")),
                new(WinPath.Combine(f.LocalAppData, @"Microsoft\Windows\WER\ReportQueue")),
            ],
        },
        new()
        {
            Id = "dumps", Name = "Crash dumps",
            Description = "Memory dumps written when programs crash.",
            Locations = [new(WinPath.Combine(f.LocalAppData, "CrashDumps")), new(WinPath.Combine(f.WindowsDir, "Minidump"))],
        },
        new()
        {
            Id = "thumbs", Name = "Thumbnail cache",
            Description = "Explorer's cached thumbnails. Windows rebuilds them when needed.",
            Locations = [new(WinPath.Combine(f.LocalAppData, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", false)],
            EnabledByDefault = false,
        },
        new()
        {
            Id = "shader", Name = "DirectX shader cache",
            Description = "Compiled graphics shaders. Games rebuild them on next launch.",
            Locations = [new(WinPath.Combine(f.LocalAppData, "D3DSCache")), new(WinPath.Combine(f.LocalAppData, @"NVIDIA\DXCache")),
                         new(WinPath.Combine(f.LocalAppData, @"AMD\DxCache"))],
            EnabledByDefault = false,
        },
        new()
        {
            Id = "wu", Name = "Windows Update downloads",
            Description = "Update packages that were already installed.",
            Locations = [new(WinPath.Combine(f.WindowsDir, @"SoftwareDistribution\Download"))], MinAge = TimeSpan.FromDays(3),
            EnabledByDefault = false,
        },
        new()
        {
            Id = "chrome", Name = "Google Chrome cache",
            Description = "Cached web content. Close Chrome first.",
            Locations =
            [
                new(WinPath.Combine(f.LocalAppData, @"Google\Chrome\User Data\*\Cache")),
                new(WinPath.Combine(f.LocalAppData, @"Google\Chrome\User Data\*\Code Cache")),
                new(WinPath.Combine(f.LocalAppData, @"Google\Chrome\User Data\*\GPUCache")),
            ],
        },
        new()
        {
            Id = "edge", Name = "Microsoft Edge cache",
            Description = "Cached web content. Close Edge first.",
            Locations =
            [
                new(WinPath.Combine(f.LocalAppData, @"Microsoft\Edge\User Data\*\Cache")),
                new(WinPath.Combine(f.LocalAppData, @"Microsoft\Edge\User Data\*\Code Cache")),
                new(WinPath.Combine(f.LocalAppData, @"Microsoft\Edge\User Data\*\GPUCache")),
            ],
        },
        new()
        {
            Id = "firefox", Name = "Mozilla Firefox cache",
            Description = "Cached web content. Close Firefox first.",
            Locations = [new(WinPath.Combine(f.LocalAppData, @"Mozilla\Firefox\Profiles\*\cache2"))],
        },
        new()
        {
            Id = "brave", Name = "Brave cache",
            Description = "Cached web content. Close Brave first.",
            Locations =
            [
                new(WinPath.Combine(f.LocalAppData, @"BraveSoftware\Brave-Browser\User Data\*\Cache")),
                new(WinPath.Combine(f.LocalAppData, @"BraveSoftware\Brave-Browser\User Data\*\Code Cache")),
            ],
        },
    ];

    /// <summary>Expands '*' path segments (e.g. "...\User Data\*\Cache") into existing folders.</summary>
    public IEnumerable<string> ExpandPattern(string pattern)
    {
        var parts = WinPath.Normalize(pattern).Split('\\');
        IEnumerable<string> current = [parts[0]];
        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            current = part == "*"
                ? current.SelectMany(fs.EnumerateDirectories).ToList()
                : current.Select(c => c.EndsWith(':') ? c + "\\" + part : WinPath.Combine(c, part)).Where(fs.DirectoryExists).ToList();
        }
        return current.Where(fs.DirectoryExists);
    }

    public JunkScanResult Scan(JunkCategory category, DateTime nowUtc, CancellationToken ct = default)
    {
        var result = new JunkScanResult { Category = category };
        foreach (var loc in category.Locations)
            foreach (var root in ExpandPattern(loc.Pattern))
            {
                result.Roots.Add(root);
                foreach (var file in Enumerate(root, loc.Recursive, ct))
                {
                    if (!MatchesFilter(WinPath.GetFileName(file), loc.FileFilter)) continue;
                    try
                    {
                        if (category.MinAge > TimeSpan.Zero && nowUtc - fs.GetLastWriteTimeUtc(file) < category.MinAge) continue;
                        result.TotalBytes += fs.GetFileSize(file);
                        result.Files.Add(file);
                    }
                    catch { /* file vanished */ }
                }
            }
        return result;
    }

    public JunkCleanResult Clean(JunkScanResult scan, CancellationToken ct = default)
    {
        var r = new JunkCleanResult();
        foreach (var file in scan.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                long size = fs.GetFileSize(file);
                fs.DeleteFile(file, toRecycleBin: false);
                r.FilesDeleted++;
                r.BytesFreed += size;
            }
            catch { r.FilesSkipped++; }
        }
        foreach (var root in scan.Roots) DeleteEmptySubfolders(root);
        return r;
    }

    private IEnumerable<string> Enumerate(string root, bool recursive, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var f in fs.EnumerateFiles(dir)) yield return f;
            if (recursive)
                foreach (var d in fs.EnumerateDirectories(dir)) stack.Push(d);
        }
    }

    private void DeleteEmptySubfolders(string root)
    {
        foreach (var d in fs.EnumerateDirectories(root).ToList())
        {
            DeleteEmptySubfolders(d);
            try
            {
                if (!fs.EnumerateFiles(d).Any() && !fs.EnumerateDirectories(d).Any()) fs.DeleteDirectory(d, toRecycleBin: false);
            }
            catch { /* in use */ }
        }
    }

    private static bool MatchesFilter(string name, string filter)
    {
        if (filter == "*") return true;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
