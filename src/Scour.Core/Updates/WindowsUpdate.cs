using System.Globalization;
using System.Text.Json;
using Scour.Core.Platform;

namespace Scour.Core.Updates;

public enum UpdateResult
{
    NotStarted = 0,
    InProgress = 1,
    Succeeded = 2,
    SucceededWithErrors = 3,
    Failed = 4,
    Aborted = 5,
}

public sealed record UpdateHistoryEntry(string Title, DateTime DateUtc, UpdateResult Result, int HResult, bool IsUninstall)
{
    public bool IsFailure => Result is UpdateResult.Failed or UpdateResult.Aborted;

    /// <summary>"0x80070643" for failures, the code to search for.</summary>
    public string? ErrorCode => IsFailure && HResult != 0 ? $"0x{HResult:X8}" : null;
}

public sealed record PendingUpdate(string Id, string Title, bool IsDriver, long SizeBytes, bool IsHidden);

/// <summary>
/// Pausing Windows Update the way Settings does (the same registry values, so Settings shows the pause and
/// can resume it), and reading what the Windows Update Agent reports through PowerShell.
/// </summary>
public sealed class WindowsUpdatePause(IRegistry registry)
{
    /// <summary>Settings offers at most five weeks; Windows ignores longer pauses.</summary>
    public const int MaxDays = 35;

    private static readonly RegKeyPath Key = RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");

    private static readonly string[] StartValues = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    private static readonly string[] EndValues = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];

    /// <summary>When the current pause ends, or null if updates aren't paused.</summary>
    public DateTime? PausedUntilUtc(DateTime nowUtc)
    {
        var end = registry.GetString(Key, "PauseUpdatesExpiryTime");
        if (end is null || !DateTime.TryParse(end, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return null;
        return t > nowUtc ? t : null;
    }

    public DateTime Pause(int days, DateTime nowUtc)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var until = nowUtc.AddDays(days);
        string start = Iso(nowUtc), end = Iso(until);
        foreach (var name in StartValues) registry.SetValue(Key, name, RegValue.String(start));
        foreach (var name in EndValues) registry.SetValue(Key, name, RegValue.String(end));
        return until;
    }

    public void Resume()
    {
        foreach (var name in StartValues.Concat(EndValues)) registry.DeleteValue(Key, name);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>Parses the JSON the update scripts print (ConvertTo-Json gives an object for one item, an array for more).</summary>
public static class WindowsUpdateJson
{
    public static List<UpdateHistoryEntry> History(string json) => Items(json).Select(e =>
    {
        DateTime.TryParse(Str(e, "Date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date);
        int result = Int(e, "Result");
        return new UpdateHistoryEntry(Str(e, "Title"), date, Enum.IsDefined((UpdateResult)result) ? (UpdateResult)result : UpdateResult.NotStarted,
                                      Int(e, "HResult"), Int(e, "Operation") == 2);
    }).Where(h => h.Title.Length > 0).OrderByDescending(h => h.DateUtc).ToList();

    public static List<PendingUpdate> Pending(string json) => Items(json)
        .Select(e => new PendingUpdate(Str(e, "Id"), Str(e, "Title"), e.TryGetProperty("Driver", out var d) && d.ValueKind == JsonValueKind.True,
                                       e.TryGetProperty("Size", out var s) && s.TryGetInt64(out var size) ? size : 0,
                                       e.TryGetProperty("Hidden", out var h) && h.ValueKind == JsonValueKind.True))
        .Where(p => p.Id.Length > 0 && p.Title.Length > 0).ToList();

    private static List<JsonElement> Items(string json)
    {
        json = json.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root])
               .Where(e => e.ValueKind == JsonValueKind.Object).Select(e => e.Clone()).ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? unchecked((int)n) : 0;
}
