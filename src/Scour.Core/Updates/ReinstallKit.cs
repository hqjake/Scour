using System.Text.Json;

namespace Scour.Core.Updates;

public sealed record KitApp(string Id, string Source, string? Version);

/// <summary>Reads and writes the package list produced by "winget export" (the reinstall kit).</summary>
public static class ReinstallKit
{
    public static List<KitApp> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var apps = new List<KitApp>();
        if (!doc.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return apps;
        foreach (var src in sources.EnumerateArray())
        {
            var name = src.TryGetProperty("SourceDetails", out var d) && d.TryGetProperty("Name", out var n) ? n.GetString() ?? "winget" : "winget";
            if (!src.TryGetProperty("Packages", out var pkgs) || pkgs.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in pkgs.EnumerateArray())
            {
                if (!p.TryGetProperty("PackageIdentifier", out var id) || id.GetString() is not { Length: > 0 } pid) continue;
                var ver = p.TryGetProperty("Version", out var v) ? v.GetString() : null;
                apps.Add(new KitApp(pid, name, ver));
            }
        }
        return apps.DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The same winget export document with only the chosen packages left in it.</summary>
    public static string Filter(string json, IReadOnlySet<string> keepIds)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        if (root["Sources"] is System.Text.Json.Nodes.JsonArray sources)
        {
            foreach (var src in sources.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                if (src["Packages"] is not System.Text.Json.Nodes.JsonArray pkgs) continue;
                for (int i = pkgs.Count - 1; i >= 0; i--)
                    if (pkgs[i]?["PackageIdentifier"]?.GetValue<string>() is not { } id || !keepIds.Contains(id)) pkgs.RemoveAt(i);
            }
            for (int i = sources.Count - 1; i >= 0; i--)
                if (sources[i]?["Packages"] is System.Text.Json.Nodes.JsonArray { Count: 0 }) sources.RemoveAt(i);
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Apps from the kit that aren't installed here yet.</summary>
    public static List<KitApp> Missing(IEnumerable<KitApp> kit, IEnumerable<KitApp> installedHere)
    {
        var have = installedHere.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return kit.Where(a => !have.Contains(a.Id)).ToList();
    }

    public static string InstallArguments(KitApp app) =>
        $"install --id \"{app.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --source {app.Source}";

    /// <summary>"Microsoft.VisualStudioCode" → "Visual Studio Code", "Python.Python.3.12" → "Python 3.12".</summary>
    public static string FriendlyName(string id)
    {
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return id;
        var sb = new System.Text.StringBuilder();
        bool prevNumeric = false;
        foreach (var part in parts.Skip(1))
        {
            bool numeric = part.All(char.IsDigit);
            if (sb.Length > 0) sb.Append(numeric && prevNumeric ? '.' : ' ');
            sb.Append(numeric ? part : SplitCamel(part));
            prevNumeric = numeric;
        }
        return sb.ToString();
    }

    private static string SplitCamel(string word)
    {
        var sb = new System.Text.StringBuilder(word.Length + 4);
        for (int i = 0; i < word.Length; i++)
        {
            if (i > 0 && char.IsUpper(word[i]) && char.IsLower(word[i - 1])) sb.Append(' ');
            sb.Append(word[i]);
        }
        return sb.ToString();
    }

    public static string ExportArguments(string path) =>
        $"export -o \"{path}\" --accept-source-agreements --disable-interactivity";
}

public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string? DownloadUrl, string PageUrl, string? Sha256 = null);

/// <summary>Reads GitHub's "latest release" response and decides whether it's newer.</summary>
public static class ReleaseCheck
{
    public static ReleaseInfo? Parse(string json, string assetName = "Scour.exe")
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;
        string? url = null, sha = null;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    // GitHub publishes "sha256:<hex>" for every asset.
                    if (a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = d["sha256:".Length..].ToLowerInvariant();
                }
        return new ReleaseInfo(version, tag, r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                               r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", url,
                               r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "", sha);
    }

    /// <summary>"v1.2.3" / "1.2" / "1.2.3-beta" → 1.2.3 (pre-release suffixes are ignored).</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var t = text.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        if (int.TryParse(t, out var major))
        {
            version = new Version(major, 0, 0);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNewer(ReleaseInfo release, Version current) =>
        release.Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));
}
