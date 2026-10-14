using System.Text;

namespace Scour.Core.Repair;

/// <summary>
/// Turns a console tool's raw output into lines. Windows tools disagree on encoding: sfc and
/// chkdsk write UTF-16, most others the OEM code page; progress is drawn with carriage returns.
/// </summary>
public sealed class OutputDecoder
{
    private readonly StringBuilder _line = new();
    private Encoding? _encoding;
    private Decoder? _decoder;
    private string? _lastProgress;

    /// <summary>Feeds raw bytes; returns completed lines (progress updates replace each other).</summary>
    public IEnumerable<string> Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return [];
        if (_encoding is null)
        {
            _encoding = LooksLikeUtf16(bytes) ? Encoding.Unicode : Encoding.UTF8;
            _decoder = _encoding.GetDecoder();
        }
        var chars = new char[_encoding.GetMaxCharCount(bytes.Length)];
        int n = _decoder!.GetChars(bytes, chars, flush: false);
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

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return true;
        int pairs = Math.Min(b.Length / 2, 64), zeros = 0;
        for (int i = 0; i < pairs; i++) if (b[i * 2 + 1] == 0 && b[i * 2] != 0) zeros++;
        return pairs > 0 && zeros * 10 >= pairs * 6;
    }
}
