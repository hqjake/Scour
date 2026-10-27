using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Platform;
using Scour.App.Services;

namespace Scour.App.ViewModels;

/// <summary>One reading on the Sensors page: a label, a value and, for temperatures, how worried to be.</summary>
public sealed record SensorRow(string Label, string Value, double? Percent = null, int Level = 0);

public sealed partial class SensorGroup(string title, string glyph) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    [ObservableProperty] private IReadOnlyList<SensorRow> _rows = [];
}

/// <summary>Temperatures, fans and load, refreshed every two seconds while the page is open.</summary>
public sealed partial class SensorsViewModel : PageViewModel
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private SensorReader? _reader;
    private bool _busy;

    public SensorsViewModel() => _timer.Tick += (_, _) => SampleAsync().Forget("Reading sensors", report: false);

    public override string Title => "Sensors";
    public override string Glyph => "";
    public override string Group => "System";

    public ObservableCollection<SensorGroup> Groups { get; } = [];

    protected override async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _reader ??= await Task.Run(() => new SensorReader());
            await SampleAsync();
            StatusText = "CPU core temperatures need a kernel driver, which Scour doesn't install (security software rightly distrusts them). " +
                         "GPU, drive and motherboard (ACPI) readings come straight from Windows.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public override void OnShown() => _timer.Start();
    public override void OnHidden() => _timer.Stop();

    private async Task SampleAsync()
    {
        if (_busy || _reader is null) return;
        _busy = true;
        try
        {
            var reader = _reader;
            var s = await Task.Run(() => reader.Sample());
            var groups = new List<SensorGroup>();

            var cpu = new List<SensorRow> { new("Load", $"{s.CpuPercent:0}%", s.CpuPercent) };
            if (s.CpuMHz is { } mhz) cpu.Add(new SensorRow("Speed", mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz"));
            foreach (var (zone, c) in s.ThermalZones)
                cpu.Add(new SensorRow("Temperature (" + ZoneName(zone) + ")", $"{c:0} °C", null, Level(c, 80, 95)));
            groups.Add(MakeGroup("Processor", "", cpu));

            foreach (var g in s.Gpus)
            {
                var rows = new List<SensorRow>();
                if (g.UsagePercent is { } u) rows.Add(new SensorRow("Load", $"{u:0}%", u));
                if (g.TemperatureC is { } t) rows.Add(new SensorRow("Temperature", $"{t:0} °C", null, Level(t, 83, 92)));
                if (g.FanRpm is { } f) rows.Add(new SensorRow("Fan", $"{f:N0} RPM"));
                if (g.PowerPercent is { } p) rows.Add(new SensorRow("Power", $"{p:0}% of maximum", p));
                if (g.DedicatedMemoryUsed is { } m) rows.Add(new SensorRow("Video memory in use", Format.Bytes(m)));
                if (rows.Count == 0) rows.Add(new SensorRow("Readings", "Its driver doesn't report any"));
                groups.Add(MakeGroup(g.Name, "", rows));
            }

            double memPct = s.MemoryTotal == 0 ? 0 : 100.0 * s.MemoryUsed / s.MemoryTotal;
            groups.Add(MakeGroup("Memory", "", [new SensorRow("In use", $"{Format.Bytes((long)s.MemoryUsed)} of {Format.Bytes((long)s.MemoryTotal)}", memPct)]));

            if (s.Drives.Count > 0)
                groups.Add(MakeGroup("Drives", "", s.Drives.Select(d => new SensorRow(d.Name, $"{d.TemperatureC} °C", null, Level(d.TemperatureC, 55, 65))).ToList()));

            // Update in place so the page doesn't flicker.
            for (int i = 0; i < groups.Count; i++)
            {
                if (i < Groups.Count && Groups[i].Title == groups[i].Title) Groups[i].Rows = groups[i].Rows;
                else if (i < Groups.Count) Groups[i] = groups[i];
                else Groups.Add(groups[i]);
            }
            while (Groups.Count > groups.Count) Groups.RemoveAt(Groups.Count - 1);
        }
        finally
        {
            _busy = false;
        }
    }

    private static SensorGroup MakeGroup(string title, string glyph, IReadOnlyList<SensorRow> rows) => new(title, glyph) { Rows = rows };

    /// <summary>0 fine, 1 warm, 2 hot.</summary>
    private static int Level(double c, double warm, double hot) => c >= hot ? 2 : c >= warm ? 1 : 0;

    private static string ZoneName(string zone)
    {
        var last = zone.Split('.', '\\').LastOrDefault(p => p.Length > 0) ?? zone;
        return last.Length is > 0 and <= 12 ? last : "ACPI";
    }
}
