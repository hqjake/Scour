using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scour.Tests;

/// <summary>The language files must stay in step with the interface they translate.</summary>
public class TranslationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scour.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Every English text a translation can apply to: fixed texts in the views and Loc.T("…") in code.</summary>
    private static HashSet<string> InterfaceTexts(string root)
    {
        var app = Path.Combine(root, "src", "Scour.App");
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(app, "Views"), "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(xaml), "\\b(?:Text|Content|Header|ToolTip|Title)=\"([^\"{][^\"]*)\""))
                texts.Add(WebUtility.HtmlDecode(m.Groups[1].Value).Trim());
        foreach (var cs in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllText(cs);
            foreach (Match m in Regex.Matches(code, "public override string (?:Title|Group) => \"([^\"]+)\"")) texts.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code, "Loc\\.T\\(\"([^\"]+)\"\\)")) texts.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code, "Loc\\.T\\(\\w+ \\? \"([^\"]+)\" : \"([^\"]+)\"\\)"))
            {
                texts.Add(m.Groups[1].Value);
                texts.Add(m.Groups[2].Value);
            }
        }
        return texts;
    }

    public static IEnumerable<object[]> Languages() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Scour.App", "Assets", "Lang"), "*.json")
                 .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory]
    [MemberData(nameof(Languages))]
    public void Language_file_is_complete_and_has_no_stale_entries(string code)
    {
        var root = RepoRoot();
        var file = Path.Combine(root, "src", "Scour.App", "Assets", "Lang", code + ".json");
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Assert.All(strings, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{code}: empty translation for \"{kv.Key}\""));

        var texts = InterfaceTexts(root);
        var stale = strings.Keys.Where(k => !texts.Contains(k)).ToList();
        Assert.True(stale.Count == 0, $"{code}.json has texts the app no longer shows: {string.Join(" | ", stale)}");

        // Everything shown should be translated, apart from names and symbols that read the same everywhere.
        var untranslatable = new HashSet<string> { "Ctrl K", "DNS", "Esc", "PID", "SCOUR", "Scour", "example.com", "CPU", "Memory", "ms PING",
                                                    "0.0.0.0 blocks the site", "DNS services", "Mbps DOWN", "Mbps UP", "pnputil output", "·  up" };
        var missing = texts.Where(t => Regex.IsMatch(t, "[A-Za-z]{2}") && !t.StartsWith("pack:", StringComparison.Ordinal) &&
                                       !strings.ContainsKey(t) && !untranslatable.Contains(t)).ToList();
        Assert.True(missing.Count == 0, $"{code}.json is missing: {string.Join(" | ", missing)}");
    }
}
