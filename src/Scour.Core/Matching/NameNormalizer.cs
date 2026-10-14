using System.Text;
using System.Text.RegularExpressions;

namespace Scour.Core.Matching;

public static class NameNormalizer
{
    private static readonly Regex Bracketed = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"(?<![A-Za-z])[vV]?\d+(\.\d+)+[A-Za-z0-9\-]*", RegexOptions.Compiled);
    private static readonly Regex Arch = new(
        @"\b(x64|x86|x86_64|amd64|arm64|win64|win32|64[- ]?bit|32[- ]?bit)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Lang = new(@"\b[a-z]{2}-[A-Z]{2}\b", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex CompanySuffix = new(
        @"\b(inc|incorporated|llc|ltd|limited|corp|corporation|co|company|gmbh|ag|sa|s\.a|s\.r\.l|srl|bv|b\.v|plc|pty|oy|ab|as|kg|sarl|software|technologies|technology|foundation|group|holdings|studios?|labs?|development|systems)\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Words too generic to identify a program on their own. A leftover is never matched by one
    /// of these alone.
    /// </summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "of", "by", "a", "an", "to", "in", "on",
        "microsoft", "windows", "common", "files", "file", "program", "programs", "software", "update", "updater",
        "updates", "setup", "install", "installer", "uninstall", "uninstaller", "service", "services", "driver",
        "drivers", "system", "data", "app", "apps", "application", "applications", "tool", "tools", "helper",
        "launcher", "runtime", "redistributable", "package", "packages", "plugin", "plugins", "bit", "edition",
        "version", "pro", "professional", "free", "home", "standard", "ultimate", "premium", "portable", "studio",
        "media", "player", "manager", "center", "centre", "client", "desktop", "online", "cloud", "network",
        "security", "user", "users", "local", "cache", "temp", "shared", "core", "framework", "sdk", "library",
        "components", "component", "support", "toolkit", "utility", "utilities", "extension", "extensions",
        "agent", "engine", "viewer", "editor", "reader", "new", "lite", "plus", "x64", "x86", "64", "32",
        "documents", "settings", "config", "configuration", "logs", "log", "bin", "lib", "assets", "resources",
        "default", "profile", "profiles", "addin", "addins", "beta", "preview", "insider", "web", "browser",
    };

    /// <summary>"Mozilla Firefox (x64 en-US) 126.0.1" → "Mozilla Firefox".</summary>
    public static string CleanDisplayName(string name)
    {
        var s = Bracketed.Replace(name, " ");
        s = Version.Replace(s, " ");
        s = Arch.Replace(s, " ");
        s = Lang.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        s = s.TrimEnd('-', '–', ':', ',', '.', ' ', '_').TrimStart('-', ' ');
        return s.Length == 0 ? name.Trim() : s;
    }

    /// <summary>"Mozilla Corporation" → "Mozilla"; "The Document Foundation" → "Document".</summary>
    public static string CleanPublisher(string publisher)
    {
        var s = Bracketed.Replace(publisher, " ");
        s = CompanySuffix.Replace(s, " ");
        s = Regex.Replace(s, @"^\s*the\s+", "", RegexOptions.IgnoreCase);
        s = s.Replace(",", " ");
        s = Spaces.Replace(s, " ").Trim().TrimEnd('.', '-', ' ');
        return s.Length == 0 ? publisher.Trim() : s;
    }

    /// <summary>Lower-cases and strips everything but letters and digits ("Notepad++" → "notepadpp").</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.ToLowerInvariant()
            .Replace("++", "pp")
            .Replace("+", "plus")
            .Replace("#", "sharp")
            .Replace("&", "and");
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static IReadOnlyList<string> Words(string s)
    {
        var parts = Regex.Split(s, @"[^A-Za-z0-9\+#]+|(?<=[a-z])(?=[A-Z])");
        return parts.Select(Normalize).Where(w => w.Length > 0).ToList();
    }

    public static bool IsDistinctiveWord(string normalizedWord) =>
        normalizedWord.Length >= 4 && !StopWords.Contains(normalizedWord) && !normalizedWord.All(char.IsDigit);
}
