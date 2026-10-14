using System.Text.RegularExpressions;
using Scour.Core.Platform;

namespace Scour.Core.Programs;

public static class CommandLine
{
    private static readonly Regex ExeBoundary = new(
        @"\.(exe|com|bat|cmd)(?=\s|""|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Splits a command line such as <c>"C:\Program Files\X\unins000.exe" /SILENT</c> or the
    /// unquoted <c>C:\Program Files\X\uninst.exe /S</c> into executable and arguments.
    /// </summary>
    public static (string Executable, string Arguments) Split(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return ("", "");

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) return (s.Trim('"'), "");
            return (s[1..end], s[(end + 1)..].Trim());
        }

        var m = ExeBoundary.Match(s);
        if (m.Success)
        {
            int end = m.Index + m.Length;
            return (s[..end].Trim(), s[end..].Trim());
        }

        int space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>Parses a DisplayIcon value like <c>"C:\x\app.exe",0</c> into path and index.</summary>
    public static (string Path, int Index) ParseIconLocation(string value)
    {
        var s = value.Trim();
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var parsed))
        {
            index = parsed;
            s = s[..comma];
        }
        return (s.Trim().Trim('"'), index);
    }

    /// <summary>Extracts every absolute path that appears in arbitrary text (quoted or not).</summary>
    public static IEnumerable<string> ExtractPaths(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Regex.Matches(text, @"""([A-Za-z]:\\[^""]+)""|([A-Za-z]:\\[^\s"",;|]+)"))
        {
            var p = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            yield return WinPath.Normalize(p);
        }
    }

    public static string Quote(string path) => path.Contains(' ') && !path.StartsWith('"') ? $"\"{path}\"" : path;
}
