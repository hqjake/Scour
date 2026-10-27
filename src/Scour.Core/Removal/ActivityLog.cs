using System.Text;
using System.Text.Json;
using Scour.Core.Platform;

namespace Scour.Core.Removal;

/// <summary>How to reverse a change: a kind the app knows how to undo, and the data it needs.</summary>
public sealed record UndoInfo(string Kind, string Data);

/// <summary>One change Scour made. <see cref="Undone"/> is filled in when the log is read.</summary>
public sealed record ActivityEntry(string Id, DateTime TimeUtc, string Category, string Title, string? Detail, UndoInfo? Undo)
{
    public bool Undone { get; init; }
    public bool CanUndo => Undo is not null && !Undone;
}

/// <summary>
/// Everything Scour changed, newest first, as JSON lines in one file. Undoing an entry appends a marker
/// rather than rewriting history; the file is trimmed when it grows large.
/// </summary>
public sealed class ActivityLog(IFileSystem fs, string path)
{
    private const int MaxLines = 4000, KeepLines = 2000;
    private static readonly JsonSerializerOptions Json = new();
    private readonly object _gate = new();

    private sealed record Line(string Id, DateTime TimeUtc, string Category, string Title, string? Detail, UndoInfo? Undo, string? UndoneId);

    public ActivityEntry Record(string category, string title, string? detail = null, UndoInfo? undo = null, DateTime? nowUtc = null)
    {
        var entry = new ActivityEntry(Guid.NewGuid().ToString("N"), nowUtc ?? DateTime.UtcNow, category, title, detail, undo);
        Append(new Line(entry.Id, entry.TimeUtc, category, title, detail, undo, null));
        return entry;
    }

    public void MarkUndone(string id, DateTime? nowUtc = null) =>
        Append(new Line(Guid.NewGuid().ToString("N"), nowUtc ?? DateTime.UtcNow, "", "", null, null, id));

    public List<ActivityEntry> List()
    {
        var lines = ReadLines();
        var undone = lines.Where(l => l.UndoneId is not null).Select(l => l.UndoneId!).ToHashSet();
        return lines.Where(l => l.UndoneId is null)
                    .Select(l => new ActivityEntry(l.Id, l.TimeUtc, l.Category, l.Title, l.Detail, l.Undo) { Undone = undone.Contains(l.Id) })
                    .OrderByDescending(e => e.TimeUtc).ToList();
    }

    private List<Line> ReadLines()
    {
        lock (_gate)
        {
            if (!fs.FileExists(path)) return [];
            var result = new List<Line>();
            foreach (var raw in fs.ReadAllText(path).Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                try { if (JsonSerializer.Deserialize<Line>(raw, Json) is { } l) result.Add(l); }
                catch (JsonException) { /* a line cut off by a crash */ }
            }
            return result;
        }
    }

    private void Append(Line line)
    {
        lock (_gate)
        {
            var dir = WinPath.GetDirectoryName(path);
            if (dir is not null) fs.CreateDirectory(dir);
            var text = fs.FileExists(path) ? fs.ReadAllText(path) : "";
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            lines.Add(JsonSerializer.Serialize(line, Json));
            if (lines.Count > MaxLines) lines = lines.Skip(lines.Count - KeepLines).ToList();
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l.TrimEnd('\r')).Append('\n');
            fs.WriteAllText(path, sb.ToString());
        }
    }
}
