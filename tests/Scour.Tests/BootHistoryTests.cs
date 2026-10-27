using Scour.Core.SystemInfo;

namespace Scour.Tests;

public class BootHistoryTests
{
    private static string Event(int id, string time, params (string Name, string Value)[] data) =>
        $"""<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Diagnostics-Performance"/><EventID>{id}</EventID><TimeCreated SystemTime="{time}"/></System><EventData>{string.Concat(data.Select(d => $"<Data Name=\"{d.Name}\">{d.Value}</Data>"))}</EventData></Event>""";

    [Fact]
    public void Parses_boots_and_delays_from_event_xml()
    {
        var (boots, delays) = BootHistory.Parse(
        [
            Event(100, "2026-10-02T08:00:00.0000000Z", ("BootTime", "45000"), ("MainPathBootTime", "20000"), ("BootPostBootTime", "25000")),
            Event(100, "2026-10-01T08:00:00.0000000Z", ("BootTime", "30500"), ("MainPathBootTime", "15000"), ("BootPostBootTime", "15500")),
            Event(101, "2026-10-02T08:00:01Z", ("Name", "OneDrive.exe"), ("FriendlyName", "Microsoft OneDrive"), ("TotalTime", "9000"), ("DegradationTime", "6000")),
            Event(103, "2026-10-02T08:00:02Z", ("Name", "SomeService"), ("FriendlyName", ""), ("TotalTime", "4000"), ("DegradationTime", "0")),
            Event(100, "2026-10-03T08:00:00Z", ("BootTime", "-1")),
            "<not xml",
            Event(999, "2026-10-03T08:00:00Z"),
        ]);
        Assert.Equal([30.5, 45.0], boots.Select(b => b.Total.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(20), boots[1].MainPath);
        Assert.Equal(2, delays.Count);
        Assert.Equal((BootDelayKind.App, "Microsoft OneDrive", 6.0), (delays[0].Kind, delays[0].FriendlyName, delays[0].Delay.TotalSeconds));
        Assert.Equal(("SomeService", 4.0), (delays[1].FriendlyName, delays[1].Delay.TotalSeconds)); // no degradation given: total time
    }

    [Fact]
    public void Slow_starters_are_grouped_and_ranked()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        BootDelay D(string name, double s, int day) => new(t.AddDays(day), BootDelayKind.App, name, name, TimeSpan.FromSeconds(s));
        var list = BootHistory.SlowStarters([D("a.exe", 2, 0), D("A.EXE", 4, 1), D("b.exe", 10, 2)]);
        Assert.Equal(["b.exe", "a.exe"], list.Select(s => s.Name.ToLowerInvariant()));
        Assert.Equal((2, 3.0), (list[1].Times, list[1].AverageDelay.TotalSeconds));
    }

    [Fact]
    public void Trend_needs_a_clear_difference()
    {
        BootRecord B(double s) => new(DateTime.UtcNow, TimeSpan.FromSeconds(s), TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(1, BootHistory.Trend([B(30), B(30), B(30), B(30), B(30), B(50), B(50), B(50), B(50), B(50)]));
        Assert.Equal(-1, BootHistory.Trend([B(50), B(50), B(50), B(50), B(50), B(30), B(30), B(30), B(30), B(30)]));
        Assert.Equal(0, BootHistory.Trend([B(30), B(31), B(30), B(29), B(30), B(31), B(30), B(31), B(32), B(30)]));
        Assert.Equal(0, BootHistory.Trend([B(10), B(90)]));
    }
}
