using System.Text;

namespace Scour.Core.Programs;

/// <summary>Helpers for Windows Installer product codes.</summary>
public static class MsiGuid
{
    public static bool TryNormalize(string? text, out string guid)
    {
        guid = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Guid.TryParse(text.Trim(), out var g)) return false;
        guid = g.ToString("B").ToUpperInvariant();
        return true;
    }

    /// <summary>
    /// Converts {12345678-90AB-CDEF-1234-567890ABCDEF} to the "packed" form used under
    /// Installer\Products: 87654321BA09FEDC21436587 09BADCFE (no separators).
    /// </summary>
    public static string Pack(string productCode)
    {
        if (!TryNormalize(productCode, out var g)) throw new FormatException($"Invalid GUID: {productCode}");
        var hex = g.Trim('{', '}').Replace("-", "");
        var sb = new StringBuilder(32);
        sb.Append(Reverse(hex[..8]));
        sb.Append(Reverse(hex[8..12]));
        sb.Append(Reverse(hex[12..16]));
        for (int i = 16; i < 32; i += 2)
        {
            sb.Append(hex[i + 1]);
            sb.Append(hex[i]);
        }
        return sb.ToString();
    }

    public static string Unpack(string packed)
    {
        if (packed.Length != 32) throw new FormatException("Packed GUID must be 32 hex characters.");
        var sb = new StringBuilder(38);
        sb.Append('{');
        sb.Append(Reverse(packed[..8])).Append('-');
        sb.Append(Reverse(packed[8..12])).Append('-');
        sb.Append(Reverse(packed[12..16])).Append('-');
        for (int i = 16; i < 32; i += 2)
        {
            if (i == 20) sb.Append('-');
            sb.Append(packed[i + 1]).Append(packed[i]);
        }
        sb.Append('}');
        return sb.ToString().ToUpperInvariant();
    }

    private static string Reverse(string s)
    {
        var a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }
}
