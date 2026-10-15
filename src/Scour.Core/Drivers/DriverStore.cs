using System.Globalization;
using Scour.Core.Platform;

namespace Scour.Core.Drivers;

/// <summary>The [Version] facts of a driver INF.</summary>
public sealed record InfInfo(string Class, string Provider, DateTime? Date, Version? Version);

public static class InfParser
{
    public static InfInfo Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current)) sections[name] = current = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            current.TryAdd(line[..eq].Trim(), line[(eq + 1)..].Trim());
        }

        // Prefer US English strings, then the neutral ones.
        var strings = sections.GetValueOrDefault("Strings.0409") ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("Strings", out var neutral))
            foreach (var (k, v) in neutral) strings.TryAdd(k, v);

        string Resolve(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = Unquote(value);
            if (value.Length > 2 && value[0] == '%' && value[^1] == '%' && strings.TryGetValue(value[1..^1], out var s)) value = Unquote(s);
            return value;
        }

        var version = sections.GetValueOrDefault("Version");
        var cls = Resolve(version?.GetValueOrDefault("Class"));
        var provider = Resolve(version?.GetValueOrDefault("Provider"));
        DateTime? date = null;
        Version? ver = null;
        if (version?.GetValueOrDefault("DriverVer") is { } dv)
        {
            var parts = Resolve(dv).Split(',', StringSplitOptions.TrimEntries);
            if (DateTime.TryParseExact(parts[0], ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
            if (parts.Length > 1 && Version.TryParse(parts[1], out var v)) ver = v;
        }
        return new InfInfo(cls, provider, date, ver);
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ';' && !quoted) return line[..i];
        }
        return line;
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
}

public enum DriverState
{
    InUse,

    /// <summary>A newer version of the same driver is installed; this one is just taking space.</summary>
    Old,

    /// <summary>Not used by any device right now, but it's the newest copy (e.g. for a device that's unplugged).</summary>
    NotInUse,
}

public sealed record DriverPackage(
    string PublishedName, string OriginalName, InfInfo Info, long SizeBytes, IReadOnlyList<string> Devices)
{
    public bool InUse => Devices.Count > 0;
}

public static class DriverStore
{
    /// <summary>"nv_dispig.inf_amd64_1a2b3c4d" → "nv_dispig.inf".</summary>
    public static string OriginalName(string packageId)
    {
        int i = packageId.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? packageId[..(i + 4)] : packageId;
    }

    /// <summary>
    /// Classifies packages. Within one driver (same original INF, provider and class), anything not in use that is
    /// older than the newest copy is "old". The newest copy is always kept, used or not, so devices that are
    /// unplugged today still work when they come back.
    /// </summary>
    public static Dictionary<string, DriverState> Classify(IEnumerable<DriverPackage> packages)
    {
        var result = new Dictionary<string, DriverState>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in packages.GroupBy(p => (p.OriginalName.ToLowerInvariant(), p.Info.Provider.ToLowerInvariant(), p.Info.Class.ToLowerInvariant())))
        {
            var newest = group.OrderByDescending(p => p.Info.Version ?? new Version(0, 0)).ThenByDescending(p => p.Info.Date ?? DateTime.MinValue).First();
            foreach (var p in group)
            {
                result[p.PublishedName] = p.InUse ? DriverState.InUse
                                        : p == newest ? DriverState.NotInUse
                                        : IsOlder(p, newest) ? DriverState.Old
                                        : DriverState.NotInUse; // same version twice: leave it alone
            }
        }
        return result;
    }

    private static bool IsOlder(DriverPackage a, DriverPackage b)
    {
        if (a.Info.Version is { } va && b.Info.Version is { } vb && va != vb) return va < vb;
        return a.Info.Date is { } da && b.Info.Date is { } db && da < db;
    }

    public static string DeleteArguments(string publishedName) => $"/delete-driver {publishedName}"; // no /force: Windows refuses if in use
    public static string ExportArguments(string folder) => $"/export-driver * \"{folder}\"";
    public static string RestoreArguments(string folder) => $"/add-driver \"{WinPath.Combine(folder, "*.inf")}\" /subdirs /install";
}
