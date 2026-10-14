namespace Scour.Core.Platform;

public enum RegHive
{
    LocalMachine,
    CurrentUser,
    ClassesRoot,
    Users,
}

/// <summary>
/// A registry key location. Always refers to the native (64-bit) view; 32-bit entries are
/// addressed explicitly through their WOW6432Node path, exactly as regedit shows them.
/// </summary>
public readonly record struct RegKeyPath(RegHive Hive, string SubPath)
{
    public static RegKeyPath LocalMachine(string subPath) => new(RegHive.LocalMachine, subPath);
    public static RegKeyPath CurrentUser(string subPath) => new(RegHive.CurrentUser, subPath);

    public string Name
    {
        get
        {
            int i = SubPath.LastIndexOf('\\');
            return i < 0 ? SubPath : SubPath[(i + 1)..];
        }
    }

    public RegKeyPath? Parent
    {
        get
        {
            if (SubPath.Length == 0) return null;
            int i = SubPath.LastIndexOf('\\');
            return new RegKeyPath(Hive, i < 0 ? "" : SubPath[..i]);
        }
    }

    public int Depth => SubPath.Length == 0 ? 0 : SubPath.Count(c => c == '\\') + 1;

    public RegKeyPath Child(string name) =>
        new(Hive, SubPath.Length == 0 ? name : SubPath + "\\" + name);

    public bool IsSameOrUnder(RegKeyPath other) =>
        Hive == other.Hive &&
        (string.Equals(SubPath, other.SubPath, StringComparison.OrdinalIgnoreCase) ||
         other.SubPath.Length == 0 ||
         SubPath.StartsWith(other.SubPath + "\\", StringComparison.OrdinalIgnoreCase));

    public string HiveShortName => Hive switch
    {
        RegHive.LocalMachine => "HKLM",
        RegHive.CurrentUser => "HKCU",
        RegHive.ClassesRoot => "HKCR",
        RegHive.Users => "HKU",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string HiveLongName => Hive switch
    {
        RegHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegHive.CurrentUser => "HKEY_CURRENT_USER",
        RegHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegHive.Users => "HKEY_USERS",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public string LongPath => SubPath.Length == 0 ? HiveLongName : HiveLongName + "\\" + SubPath;

    public override string ToString() => SubPath.Length == 0 ? HiveShortName : HiveShortName + "\\" + SubPath;

    public static bool TryParse(string text, out RegKeyPath path)
    {
        path = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().TrimEnd('\\');
        if (text.StartsWith("Computer\\", StringComparison.OrdinalIgnoreCase)) text = text[9..];
        int i = text.IndexOf('\\');
        string hive = i < 0 ? text : text[..i];
        string sub = i < 0 ? "" : text[(i + 1)..];
        RegHive? h = hive.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegHive.Users,
            _ => null,
        };
        if (h is null) return false;
        path = new RegKeyPath(h.Value, sub);
        return true;
    }

    public static RegKeyPath Parse(string text) =>
        TryParse(text, out var p) ? p : throw new FormatException($"Not a registry path: {text}");

    public bool Equals(RegKeyPath other) =>
        Hive == other.Hive && string.Equals(SubPath, other.SubPath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Hive, StringComparer.OrdinalIgnoreCase.GetHashCode(SubPath));
}
