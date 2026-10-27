using System.Diagnostics.Eventing.Reader;
using Scour.Core.SystemInfo;

namespace Scour.App.Platform;

public static class BootEvents
{
    /// <summary>The recorded boots and startup delays, newest events first, up to <paramref name="max"/> events.</summary>
    public static (List<BootRecord> Boots, List<BootDelay> Delays) Read(int max = 600)
    {
        var xml = new List<string>();
        var query = new EventLogQuery(BootHistory.LogName, PathType.LogName, BootHistory.Query) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        for (var e = reader.ReadEvent(); e is not null && xml.Count < max; e = reader.ReadEvent())
            using (e)
                xml.Add(e.ToXml());
        return BootHistory.Parse(xml);
    }
}
