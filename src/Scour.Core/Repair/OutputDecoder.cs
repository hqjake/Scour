using System.Text;

namespace Scour.Core.Repair;

/// <summary>
/// Turns a console tool's raw output into lines. Windows tools disagree on encoding: sfc and
/// chkdsk write UTF-16, PowerShell and winget UTF-8, most others the OEM code page (cp850, cp866…);
/// progress is drawn with carriage returns.
/// </summary>
public sealed class OutputDecoder
{
    private readonly StringBuilder _line = new();
    private readonly Encoding _fallback;
    private Encoding? _encoding;
    private Decoder? _decoder;
    private string? _lastProgress;

    /// <param name="fallback">Used when the output is neither UTF-16 nor valid UTF-8 — the OEM code page
    /// on Windows. Without one, UTF-8 is assumed.</param>
    public OutputDecoder(Encoding? fallback = null) => _fallback = fallback ?? Encoding.UTF8;

    /// <summary>Feeds raw bytes; returns completed lines (progress updates replace each other).</summary>
    public IEnumerable<string> Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return [];
        if (_encoding is null)
        {
            if (LooksLikeUtf16(bytes)) _encoding = Encoding.Unicode;
            // ASCII reads the same in every candidate, so wait for the first non-ASCII byte to decide.
            else if (bytes.ContainsAnyExceptInRange((byte)0, (byte)0x7F)) _encoding = IsUtf8(bytes) ? Encoding.UTF8 : _fallback;
            if (_encoding is not null) _decoder = _encoding.GetDecoder();
        }
        var decoder = _decoder ?? Encoding.ASCII.GetDecoder();
        var chars = new char[(_encoding ?? Encoding.ASCII).GetMaxCharCount(bytes.Length)];
        int n = decoder.GetChars(bytes, chars, flush: false);
        var lines = new List<string>();
        for (int i = 0; i < n; i++)
        {
            char c = chars[i];
            if (c == '\0' || c == '\uFEFF') continue;
            if (c == '\n')
            {
                Emit(lines, isProgress: false);
            }
            else if (c == '\r')
            {
                // A lone CR redraws the line in place (progress); CRLF is a normal line end.
                bool crlf = i + 1 < n && chars[i + 1] == '\n';
                if (!crlf) Emit(lines, isProgress: true);
            }
            else
            {
                _line.Append(c);
            }
        }
        return lines;
    }

    public IEnumerable<string> Flush()
    {
        var lines = new List<string>();
        Emit(lines, isProgress: false);
        return lines;
    }

    private void Emit(List<string> lines, bool isProgress)
    {
        var text = _line.ToString().TrimEnd();
        _line.Clear();
        if (text.Length == 0) return;
        if (isProgress)
        {
            if (text == _lastProgress) return;
            _lastProgress = text;
        }
        lines.Add(text);
    }

    /// <summary>Decodes a tool's complete output (see the class summary for how the encoding is chosen).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding? fallback = null)
    {
        if (bytes.IsEmpty) return "";
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        if (LooksLikeUtf16(bytes)) return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF').Replace("\0", "");
        return IsUtf8(bytes) ? Encoding.UTF8.GetString(bytes) : (fallback ?? Encoding.UTF8).GetString(bytes);
    }

    /// <summary>True if the bytes are valid UTF-8, allowing a sequence cut off at the end of the buffer.</summary>
    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        var status = System.Text.Unicode.Utf8.ToUtf16(bytes, chars, out _, out _, replaceInvalidSequences: false, isFinalBlock: false);
        return status is System.Buffers.OperationStatus.Done or System.Buffers.OperationStatus.NeedMoreData;
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return true;
        int pairs = Math.Min(b.Length / 2, 64), zeros = 0;
        for (int i = 0; i < pairs; i++) if (b[i * 2 + 1] == 0 && b[i * 2] != 0) zeros++;
        return pairs > 0 && zeros * 10 >= pairs * 6;
    }
}
