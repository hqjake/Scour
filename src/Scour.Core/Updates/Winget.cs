namespace Scour.Core.Updates;

public sealed record WingetPackage(string Name, string Id, string Version, string Available, string Source, bool RequiresExplicit);

/// <summary>Reads the tables winget prints for "winget upgrade".</summary>
public static class WingetParser
{
    public static List<WingetPackage> ParseUpgradeTable(IEnumerable<string> rawLines)
    {
        // Spinners redraw with CR; keep only what's left after the last redraw.
        var lines = rawLines.Select(l => l[(l.LastIndexOf('\r') + 1)..].TrimEnd()).ToList();
        var result = new List<WingetPackage>();
        int tableIndex = 0;
        int afterPreviousTable = 0;

        for (int i = 1; i < lines.Count; i++)
        {
            if (!IsRule(lines[i])) continue;
            var header = lines[i - 1];
            var starts = ColumnStarts(header);
            if (starts.Count < 4) continue;

            bool explicitTable = tableIndex > 0 ||
                lines.Skip(afterPreviousTable).Take(Math.Max(0, i - 1 - afterPreviousTable)).Any(l => l.Contains("explicit", StringComparison.OrdinalIgnoreCase));
            int j = i + 1;
            for (; j < lines.Count; j++)
            {
                var row = lines[j];
                if (row.Trim().Length == 0) break;
                // The name is cut to its column (winget truncates it to fit). Everything after it is
                // tokenised: Id, Available and Source never contain spaces, so the middle is the version
                // (which can, e.g. "< 2.0"). This survives values wider than their column.
                var (name, rest) = SplitAt(row, starts[1]);
                var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool hasSource = starts.Count >= 5;
                int minTokens = hasSource ? 4 : 3;
                if (tokens.Length < minTokens) break;
                string id = tokens[0];
                string source = hasSource ? tokens[^1] : "";
                string available = hasSource ? tokens[^2] : tokens[^1];
                string version = string.Join(' ', tokens[1..(hasSource ? ^2 : ^1)]);
                if (version.Length == 0) break;
                result.Add(new WingetPackage(name.TrimEnd('…').TrimEnd(), id, version, available, source, explicitTable));
            }
            tableIndex++;
            afterPreviousTable = j;
            i = j;
        }
        return result.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsRule(string line) => line.Length >= 10 && line.All(c => c is '-' or '─');

    /// <summary>Display-column positions where each header word starts.</summary>
    internal static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        int col = 0;
        bool prevSpace = true;
        foreach (var rune in header.EnumerateRunes())
        {
            bool space = rune.Value == ' ';
            if (!space && prevSpace) starts.Add(col);
            prevSpace = space;
            col += Width(rune.Value);
        }
        return starts;
    }

    /// <summary>Splits a row at a display column (CJK characters are two columns wide).</summary>
    internal static (string Left, string Right) SplitAt(string row, int column)
    {
        int col = 0, index = 0;
        foreach (var rune in row.EnumerateRunes())
        {
            if (col >= column) break;
            col += Width(rune.Value);
            index += rune.Utf16SequenceLength;
        }
        return (row[..index].Trim(), row[index..].Trim());
    }

    public static int Width(int cp) =>
        cp is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD
            ? 2
            : 1;

    /// <summary>Plain-language meaning of winget's exit codes.</summary>
    public static string DescribeExitCode(int code) => unchecked((uint)code) switch
    {
        0 => "Updated",
        0x8A15002B => "No applicable update (already up to date or needs a manual update)",
        0x8A150014 => "Package not found in the sources",
        0x8A150101 => "The app is running — close it and try again",
        0x8A150109 => "Updated — restart to finish",
        0x8A15010C => "Updated — restart required",
        0x8A150056 => "Blocked by the publisher's installer",
        0x8A150011 => "Download failed (hash mismatch)",
        _ => $"winget error 0x{code:X8}",
    };

    public static bool IsSuccess(int code) => code == 0 || unchecked((uint)code) is 0x8A150109 or 0x8A15010C;

    public static string UpgradeArguments(WingetPackage p) =>
        $"upgrade --id \"{p.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity" +
        (p.Version.StartsWith('<') || p.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? " --include-unknown" : "") +
        (string.IsNullOrEmpty(p.Source) ? "" : $" --source {p.Source}");
}
