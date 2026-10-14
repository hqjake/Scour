using Scour.Core.Platform;

namespace Scour.Core.Matching;

public enum MatchStrength
{
    None = 0,
    Weak = 1,
    Strong = 2,
    Exact = 3,
}

/// <summary>
/// Decides whether a folder or registry key name belongs to a program, based on the program's
/// name, publisher and install folder.
/// </summary>
public sealed class NameMatcher
{
    private readonly HashSet<string> _fullKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publisherKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wordPrefixes = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> FullKeys => _fullKeys;
    public IReadOnlyCollection<string> PublisherKeys => _publisherKeys;
    public IReadOnlyCollection<string> Tokens => _tokens;

    public NameMatcher(string displayName, string? publisher, string? installLocation, IEnumerable<string>? extraNames = null)
    {
        var cleanedName = NameNormalizer.CleanDisplayName(displayName);
        var publisherWords = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(publisher))
        {
            var cleanedPublisher = NameNormalizer.CleanPublisher(publisher);
            AddPublisherKey(NameNormalizer.Normalize(publisher));
            AddPublisherKey(NameNormalizer.Normalize(cleanedPublisher));
            foreach (var w in NameNormalizer.Words(cleanedPublisher)) publisherWords.Add(w);
            var first = NameNormalizer.Words(cleanedPublisher).FirstOrDefault();
            if (first is not null && first.Length >= 5 && NameNormalizer.IsDistinctiveWord(first)) AddPublisherKey(first);
        }

        AddFullKey(cleanedName);
        // "Mozilla Firefox" by "Mozilla" → also "Firefox".
        var nameWords = NameNormalizer.Words(cleanedName);
        var withoutPublisher = nameWords.Where(w => !publisherWords.Contains(w)).ToList();
        if (withoutPublisher.Count > 0 && withoutPublisher.Count < nameWords.Count)
            AddFullKey(string.Concat(withoutPublisher));
        AddWordPrefixes(nameWords);
        AddWordPrefixes(withoutPublisher);

        if (!string.IsNullOrWhiteSpace(installLocation))
            AddFullKey(WinPath.GetFileName(WinPath.Normalize(installLocation)));

        if (extraNames is not null)
            foreach (var n in extraNames)
                if (!string.IsNullOrWhiteSpace(n)) AddFullKey(NameNormalizer.CleanDisplayName(n));

        foreach (var w in withoutPublisher)
            if (w.Length >= 5 && NameNormalizer.IsDistinctiveWord(w)) _tokens.Add(w);

        // A program called just "Mozilla" must not treat the Mozilla folder as a mere publisher folder.
        _publisherKeys.ExceptWith(_fullKeys);
    }

    private void AddFullKey(string raw)
    {
        var k = NameNormalizer.Normalize(raw);
        if (k.Length >= 3 && !NameNormalizer.StopWords.Contains(k) && !k.All(char.IsDigit)) _fullKeys.Add(k);
    }

    // Publisher names like "Microsoft" are fine here: the SafetyGuard keeps shared publisher
    // containers from being deleted, and their children still need a name match.
    private void AddPublisherKey(string normalized)
    {
        if (normalized.Length >= 3) _publisherKeys.Add(normalized);
    }

    /// <summary>"Acrobat Reader DC" → "acrobatreader" (leading whole words covering most of the name).</summary>
    private void AddWordPrefixes(IReadOnlyList<string> words)
    {
        var full = string.Concat(words);
        for (int n = 1; n < words.Count; n++)
        {
            var prefix = string.Concat(words.Take(n));
            if (prefix.Length >= 5 && prefix.Length * 10 >= full.Length * 6 && !NameNormalizer.StopWords.Contains(prefix))
                _wordPrefixes.Add(prefix);
        }
    }

    public bool HasNameKeys => _fullKeys.Count > 0;

    public MatchStrength Match(string candidate)
    {
        var c = NameNormalizer.Normalize(candidate);
        if (c.Length < 3) return MatchStrength.None;
        if (_fullKeys.Contains(c)) return MatchStrength.Exact;

        // "Acrobat Reader" for "Acrobat Reader DC".
        if (_wordPrefixes.Contains(c)) return MatchStrength.Strong;

        // "FirefoxHTML-308046B0AF4A39CB": judge the leading segment, ignoring the hash suffix.
        var segment = NameNormalizer.Normalize(candidate.Split(['-', '_', '.', ' ', '(', '{'], 2)[0]);
        foreach (var k in _fullKeys)
        {
            if (k.Length < 4) continue;
            // "python312" vs "python", "acrobatreaderdc" vs "acrobatreader".
            if (c.StartsWith(k, StringComparison.Ordinal) && c.Length - k.Length <= Math.Max(6, k.Length)) return MatchStrength.Strong;
            if (segment.Length >= 4 && segment.StartsWith(k, StringComparison.Ordinal) && segment.Length - k.Length <= Math.Max(6, k.Length))
                return MatchStrength.Strong;
        }

        foreach (var k in _fullKeys)
            if (k.Length >= 6 && c.Contains(k, StringComparison.Ordinal)) return MatchStrength.Weak;

        foreach (var w in NameNormalizer.Words(candidate))
            if (_tokens.Contains(w)) return MatchStrength.Weak;

        return MatchStrength.None;
    }

    public bool MatchesPublisher(string candidate)
    {
        var c = NameNormalizer.Normalize(candidate);
        if (c.Length < 3) return false;
        if (_publisherKeys.Contains(c)) return true;
        foreach (var k in _publisherKeys)
            if (k.Length >= 5 && c.StartsWith(k, StringComparison.Ordinal) && c.Length - k.Length <= 12) return true;
        return false;
    }
}
