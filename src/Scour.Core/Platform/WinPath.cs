namespace Scour.Core.Platform;

/// <summary>
/// Windows path helpers that behave identically on every OS (System.IO.Path follows the
/// host's rules, which would break the engine's tests on Linux).
/// </summary>
public static class WinPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        path = path.Trim().Trim('"').Replace('/', '\\');
        while (path.Contains("\\\\", StringComparison.Ordinal) && !path.StartsWith("\\\\", StringComparison.Ordinal))
            path = path.Replace("\\\\", "\\");
        if (path.Length > 3) path = path.TrimEnd('\\');
        return path;
    }

    public static string Combine(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return a.TrimEnd('\\') + "\\" + b.TrimStart('\\');
    }

    public static string Combine(string a, string b, params string[] rest)
    {
        var p = Combine(a, b);
        foreach (var r in rest) p = Combine(p, r);
        return p;
    }

    public static string GetFileName(string path)
    {
        path = path.TrimEnd('\\');
        int i = path.LastIndexOf('\\');
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string GetFileNameWithoutExtension(string path)
    {
        var name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    public static string GetExtension(string path)
    {
        var name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..];
    }

    public static string? GetDirectoryName(string path)
    {
        path = Normalize(path);
        int i = path.LastIndexOf('\\');
        if (i < 0) return null;
        if (i == 2 && path.Length > 1 && path[1] == ':') return path[..3]; // "C:\"
        return path[..i];
    }

    public static bool IsRooted(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\' ||
        path.StartsWith("\\\\", StringComparison.Ordinal);

    public static bool AreSame(string a, string b) => Comparer.Equals(Normalize(a), Normalize(b));

    /// <summary>True if <paramref name="path"/> equals <paramref name="root"/> or lies beneath it.</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        if (root.Length == 0) return false;
        if (Comparer.Equals(path, root)) return true;
        return path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStrictlyUnder(string path, string root) => IsSameOrUnder(path, root) && !AreSame(path, root);

    public static int Depth(string path) => Normalize(path).TrimEnd('\\').Count(c => c == '\\');
}
