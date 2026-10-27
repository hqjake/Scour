using System.Globalization;
using System.Xml.Linq;

namespace Scour.Core.SystemInfo;

public enum BootDelayKind
{
    App,
    Driver,
    Service,
}

/// <summary>One full start of Windows: from power on to a usable desktop (main path) plus the settling after it.</summary>
public sealed record BootRecord(DateTime TimeUtc, TimeSpan Total, TimeSpan MainPath, TimeSpan PostBoot);

/// <summary>Something Windows reported as making a start slower than usual.</summary>
public sealed record BootDelay(DateTime TimeUtc, BootDelayKind Kind, string Name, string FriendlyName, TimeSpan Delay);

/// <summary>Programs, drivers and services that slow down starting Windows, averaged over the recorded boots.</summary>
public sealed record SlowStarter(BootDelayKind Kind, string Name, string FriendlyName, int Times, TimeSpan AverageDelay, DateTime LastUtc);

/// <summary>
/// Reads Windows' own boot measurements (the Diagnostics-Performance event log: event 100 for each boot,
/// 101-103 for apps, drivers and services that delayed it). The events are parsed from their XML, so the
/// numbers don't depend on the language Windows is in.
/// </summary>
public static class BootHistory
{
    public const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    public const string Query = "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103)]]";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Parses event XML (as EventRecord.ToXml() returns it). Unknown or broken events are skipped.</summary>
    public static (List<BootRecord> Boots, List<BootDelay> Delays) Parse(IEnumerable<string> eventXml)
    {
        var boots = new List<BootRecord>();
        var delays = new List<BootDelay>();
        foreach (var xml in eventXml)
        {
            try
            {
                var e = XElement.Parse(xml);
                var system = e.Element(Ns + "System");
                if (!int.TryParse(system?.Element(Ns + "EventID")?.Value, out int id)) continue;
                if (system!.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value is not { } ts ||
                    !DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                    continue;
                var data = e.Element(Ns + "EventData")?.Elements(Ns + "Data")
                            .Where(d => d.Attribute("Name") is not null)
                            .GroupBy(d => d.Attribute("Name")!.Value).ToDictionary(g => g.Key, g => g.First().Value) ?? [];
                TimeSpan Ms(string name) => long.TryParse(data.GetValueOrDefault(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
                    ? TimeSpan.FromMilliseconds(v) : TimeSpan.Zero;

                if (id == 100)
                {
                    var total = Ms("BootTime");
                    if (total > TimeSpan.Zero && total < TimeSpan.FromHours(2)) boots.Add(new BootRecord(time, total, Ms("MainPathBootTime"), Ms("BootPostBootTime")));
                }
                else
                {
                    var name = data.GetValueOrDefault("Name") ?? "";
                    if (name.Length == 0) continue;
                    var friendly = data.GetValueOrDefault("FriendlyName") is { Length: > 0 } f ? f : name;
                    var kind = id switch { 101 => BootDelayKind.App, 102 => BootDelayKind.Driver, _ => BootDelayKind.Service };
                    var delay = Ms("DegradationTime");
                    if (delay == TimeSpan.Zero) delay = Ms("TotalTime");
                    delays.Add(new BootDelay(time, kind, name, friendly, delay));
                }
            }
            catch (System.Xml.XmlException) { /* skip */ }
        }
        return (boots.OrderBy(b => b.TimeUtc).ToList(), delays);
    }

    /// <summary>Who slows startup down most: grouped by name, worst average first.</summary>
    public static List<SlowStarter> SlowStarters(IEnumerable<BootDelay> delays) =>
        delays.GroupBy(d => (d.Kind, Name: d.Name.ToLowerInvariant()))
              .Select(g => new SlowStarter(g.Key.Kind, g.First().Name, g.OrderByDescending(d => d.TimeUtc).First().FriendlyName, g.Count(),
                                           TimeSpan.FromMilliseconds(g.Average(d => d.Delay.TotalMilliseconds)), g.Max(d => d.TimeUtc)))
              .OrderByDescending(s => s.AverageDelay.TotalMilliseconds * Math.Min(s.Times, 5))
              .ToList();

    /// <summary>"Faster", "slower" or "about the same": the last 5 boots against the 5 before them.</summary>
    public static int Trend(IReadOnlyList<BootRecord> boots)
    {
        if (boots.Count < 6) return 0;
        var recent = boots.TakeLast(5).Average(b => b.Total.TotalSeconds);
        var before = boots.SkipLast(5).TakeLast(5).Average(b => b.Total.TotalSeconds);
        if (recent > before * 1.15 && recent - before > 3) return 1;
        if (recent < before * 0.87 && before - recent > 3) return -1;
        return 0;
    }
}
