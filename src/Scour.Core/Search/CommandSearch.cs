namespace Scour.Core.Search;

public sealed record CommandEntry(string Title, string Subtitle, string Category, string Glyph, IReadOnlyList<string> Keywords, object Action);

/// <summary>Ranks command palette entries for a typed query (prefix, word-start, acronym and in-order letter matches).</summary>
public static class CommandSearch
{
    public static List<CommandEntry> Find(IEnumerable<CommandEntry> entries, string query, int max = 12)
    {
        query = query.Trim();
        if (query.Length == 0) return entries.Take(max).ToList();
        var scored = entries.Select(e => (Entry: e, Score: Score(e, query))).Where(x => x.Score > 0).ToList();
        if (scored.Count == 0) return [];
        // Once something matches well, scattered-letter matches are just noise.
        int floor = scored.Max(x => x.Score) / 2;
        return scored.Where(x => x.Score >= floor)
                      .OrderByDescending(x => x.Score)
                      .ThenBy(x => x.Entry.Title.Length)
                      .Take(max)
                      .Select(x => x.Entry)
                      .ToList();
    }

    public static int Score(CommandEntry e, string query)
    {
        int best = Score(e.Title, query) * 2;
        foreach (var k in e.Keywords) best = Math.Max(best, Score(k, query));
        best = Math.Max(best, Score(e.Subtitle, query) / 2);
        return best;
    }

    /// <summary>0 = no match; higher is better.</summary>
    public static int Score(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return 0;
        var t = text.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        if (t == q) return 100;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 90;

        // Every query word must start a word in the text ("clean temp" → "Clean temporary files").
        var words = t.Split([' ', '-', '_', '.', '&', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries);
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qWords.All(qw => words.Any(w => w.StartsWith(qw, StringComparison.Ordinal)))) return 80 - Math.Min(20, words.Length);

        if (t.Contains(q, StringComparison.Ordinal)) return 60;

        // Acronym: "jc" → "Junk cleaner".
        var initials = string.Concat(words.Select(w => w[0]));
        if (q.Length >= 2 && initials.StartsWith(q.Replace(" ", ""), StringComparison.Ordinal)) return 55;

        // Letters in order: "dskan" → "disk analyzer".
        int i = 0, gaps = 0, last = -1;
        foreach (var c in q.Replace(" ", ""))
        {
            int at = t.IndexOf(c, i);
            if (at < 0) return 0;
            if (last >= 0 && at != last + 1) gaps++;
            last = at;
            i = at + 1;
        }
        // Too scattered to be what was meant ("disk" in "Microsoft Workflow Diagnostic Pack").
        int letters = q.Replace(" ", "").Length;
        if (letters < 3 || gaps > letters - 2) return 0;
        return Math.Max(1, 40 - gaps * 5);
    }
}
