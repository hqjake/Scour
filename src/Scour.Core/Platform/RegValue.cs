namespace Scour.Core.Platform;

public enum RegValueKind
{
    None,
    String,
    ExpandString,
    MultiString,
    DWord,
    QWord,
    Binary,
}

/// <summary>
/// A registry value. <see cref="Data"/> is a string (String/ExpandString), string[] (MultiString),
/// uint (DWord), ulong (QWord) or byte[] (Binary/None).
/// </summary>
public sealed record RegValue(RegValueKind Kind, object Data)
{
    public static RegValue String(string s) => new(RegValueKind.String, s);
    public static RegValue Expand(string s) => new(RegValueKind.ExpandString, s);
    public static RegValue DWord(uint v) => new(RegValueKind.DWord, v);
    public static RegValue QWord(ulong v) => new(RegValueKind.QWord, v);
    public static RegValue Binary(byte[] b) => new(RegValueKind.Binary, b);
    public static RegValue Multi(params string[] s) => new(RegValueKind.MultiString, s);

    /// <summary>Best-effort textual representation (used for matching, never for round-tripping).</summary>
    public string? AsString() => Data switch
    {
        string s => s,
        string[] arr => string.Join("\n", arr),
        uint u => u.ToString(),
        ulong ul => ul.ToString(),
        int i => i.ToString(),
        long l => l.ToString(),
        _ => null,
    };

    public long? AsInteger() => Data switch
    {
        uint u => u,
        int i => i,
        ulong ul => (long)ul,
        long l => l,
        string s when long.TryParse(s, out var p) => p,
        _ => null,
    };

    /// <summary>Stable fingerprint of kind + data, used to detect modified values in snapshots.</summary>
    public string Fingerprint()
    {
        string body = Data switch
        {
            byte[] b => Convert.ToHexString(b),
            string[] arr => string.Join("\u0001", arr),
            _ => AsString() ?? "",
        };
        return ((int)Kind).ToString() + ":" + body;
    }
}
