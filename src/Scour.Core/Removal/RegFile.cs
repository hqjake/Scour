using System.Globalization;
using System.Text;
using Scour.Core.Platform;

namespace Scour.Core.Removal;

/// <summary>Writes registry content in regedit's .reg format (Version 5.00, UTF-16).</summary>
public sealed class RegFileWriter(IRegistry registry)
{
    private readonly StringBuilder _sb = new("Windows Registry Editor Version 5.00\r\n\r\n");

    public void AddKeyTree(RegKeyPath key)
    {
        if (!registry.KeyExists(key)) return;
        AppendKey(key, registry.GetValueNames(key));
        foreach (var child in registry.GetSubKeyNames(key)) AddKeyTree(key.Child(child));
    }

    public void AddValue(RegKeyPath key, string valueName)
    {
        if (registry.GetValue(key, valueName) is null) return;
        AppendKey(key, [valueName]);
    }

    private void AppendKey(RegKeyPath key, IEnumerable<string> valueNames)
    {
        _sb.Append('[').Append(key.LongPath).Append("]\r\n");
        foreach (var name in valueNames)
        {
            var v = registry.GetValue(key, name);
            if (v is not null) _sb.Append(FormatValue(name, v)).Append("\r\n");
        }
        _sb.Append("\r\n");
    }

    public override string ToString() => _sb.ToString();

    public byte[] ToBytes()
    {
        var body = Encoding.Unicode.GetBytes(_sb.ToString());
        var result = new byte[body.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        body.CopyTo(result, 2);
        return result;
    }

    public static string FormatValue(string name, RegValue value)
    {
        var lhs = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        string rhs = value.Kind switch
        {
            RegValueKind.String => "\"" + Escape((string)value.Data) + "\"",
            RegValueKind.DWord => "dword:" + Convert.ToUInt32(value.Data).ToString("x8"),
            RegValueKind.QWord => Hex("hex(b):", BitConverter.GetBytes(Convert.ToUInt64(value.Data))),
            RegValueKind.ExpandString => Hex("hex(2):", Encoding.Unicode.GetBytes((string)value.Data + "\0")),
            RegValueKind.MultiString => Hex("hex(7):", Encoding.Unicode.GetBytes(string.Concat(((string[])value.Data).Select(s => s + "\0")) + "\0")),
            RegValueKind.Binary => Hex("hex:", (byte[])value.Data),
            RegValueKind.None => Hex("hex(0):", value.Data as byte[] ?? []),
            _ => throw new NotSupportedException(value.Kind.ToString()),
        };
        return lhs + "=" + rhs;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Hex(string prefix, byte[] bytes)
    {
        var sb = new StringBuilder(prefix);
        int lineLength = prefix.Length;
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
            lineLength += 2;
            if (i < bytes.Length - 1)
            {
                sb.Append(',');
                lineLength++;
                if (lineLength > 76)
                {
                    sb.Append("\\\r\n  ");
                    lineLength = 2;
                }
            }
        }
        return sb.ToString();
    }
}

/// <summary>Parses .reg files (as written by regedit or <see cref="RegFileWriter"/>) and applies them.</summary>
public static class RegFileReader
{
    public sealed record Entry(RegKeyPath Key, bool DeleteKey, string? ValueName, RegValue? Value, bool DeleteValue);

    public static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        return Encoding.UTF8.GetString(data);
    }

    public static List<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        var lines = JoinContinuations(text.Replace("\r\n", "\n").Split('\n'));
        RegKeyPath? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("Windows Registry Editor", StringComparison.Ordinal) || line == "REGEDIT4")
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                bool delete = line.StartsWith("[-");
                var path = RegKeyPath.Parse(line[(delete ? 2 : 1)..^1]);
                entries.Add(new Entry(path, delete, null, null, false));
                current = delete ? null : path;
                continue;
            }
            if (current is null) continue;
            var (name, rest) = SplitNameValue(line);
            if (rest == "-")
            {
                entries.Add(new Entry(current.Value, false, name, null, true));
                continue;
            }
            entries.Add(new Entry(current.Value, false, name, ParseValue(rest), false));
        }
        return entries;
    }

    /// <summary>Applies a parsed .reg file to the registry. Returns the number of operations performed.</summary>
    public static int Apply(IRegistry registry, IEnumerable<Entry> entries)
    {
        int n = 0;
        foreach (var e in entries)
        {
            if (e.DeleteKey) { registry.DeleteKeyTree(e.Key); n++; }
            else if (e.ValueName is null) { registry.CreateKey(e.Key); n++; }
            else if (e.DeleteValue) { registry.DeleteValue(e.Key, e.ValueName); n++; }
            else if (e.Value is not null) { registry.SetValue(e.Key, e.ValueName, e.Value); n++; }
        }
        return n;
    }

    private static List<string> JoinContinuations(string[] lines)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var t = sb.Length > 0 ? l.TrimStart() : l;
            if (t.EndsWith('\\') && IsHexContinuation(sb.Length > 0 ? sb + t : t))
            {
                sb.Append(t[..^1]);
                continue;
            }
            sb.Append(t);
            result.Add(sb.ToString());
            sb.Clear();
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static bool IsHexContinuation(string line)
    {
        int eq = FindAssignment(line);
        return eq > 0 && line[(eq + 1)..].TrimStart().StartsWith("hex", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindAssignment(string line)
    {
        if (line.StartsWith('@')) return line.IndexOf('=');
        if (!line.StartsWith('"')) return -1;
        for (int i = 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"') return line.IndexOf('=', i);
        }
        return -1;
    }

    private static (string Name, string Data) SplitNameValue(string line)
    {
        int eq = FindAssignment(line);
        if (eq < 0) throw new FormatException($"Malformed value line: {line}");
        var lhs = line[..eq].Trim();
        var name = lhs == "@" ? "" : Unescape(lhs[1..^1]);
        return (name, line[(eq + 1)..].Trim());
    }

    private static RegValue ParseValue(string rest)
    {
        if (rest.StartsWith('"')) return RegValue.String(Unescape(rest[1..rest.LastIndexOf('"')]));
        if (rest.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            return RegValue.DWord(uint.Parse(rest[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        int colon = rest.IndexOf(':');
        var type = rest[..colon].ToLowerInvariant();
        var hex = rest[(colon + 1)..];
        var bytes = hex.Length == 0
            ? []
            : hex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        return type switch
        {
            "hex" => RegValue.Binary(bytes),
            "hex(0)" => new RegValue(RegValueKind.None, bytes),
            "hex(2)" => RegValue.Expand(Encoding.Unicode.GetString(bytes).TrimEnd('\0')),
            "hex(7)" => RegValue.Multi(SplitMulti(Encoding.Unicode.GetString(bytes))),
            "hex(b)" => RegValue.QWord(BitConverter.ToUInt64(Pad(bytes, 8))),
            "hex(4)" => RegValue.DWord(BitConverter.ToUInt32(Pad(bytes, 4))),
            _ => RegValue.Binary(bytes),
        };
    }

    private static string[] SplitMulti(string s)
    {
        var parts = s.Split('\0').ToList();
        while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts.ToArray();
    }

    private static byte[] Pad(byte[] b, int n) => b.Length >= n ? b : b.Concat(new byte[n - b.Length]).ToArray();

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
