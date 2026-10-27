using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Platform;
using Scour.App.Services;
using Scour.Core.SystemInfo;

namespace Scour.App.ViewModels;

public sealed record BootBar(string Label, double Seconds, double Height, string ToolTip, bool IsLatest);

public sealed record SlowStarterRow(SlowStarter Item)
{
    public string Name => Item.FriendlyName;
    public string Kind => Item.Kind switch { BootDelayKind.App => "Startup app", BootDelayKind.Driver => "Driver", _ => "Service" };
    public string Delay => $"+{Item.AverageDelay.TotalSeconds:0.0} s";
    public string Seen => Item.Times == 1 ? $"once, {Item.LastUtc.ToLocalTime():d}" : $"{Item.Times} times, last {Item.LastUtc.ToLocalTime():d}";
}

/// <summary>How long Windows takes to start, over time, and what slows it down.</summary>
public sealed partial class BootTimeViewModel(Action<Type> navigate) : PageViewModel
{
    private const double ChartHeight = 150;

    public override string Title => "Boot time";
    public override string Glyph => "";
    public override string Group => "System";

    public ObservableCollection<BootBar> Bars { get; } = [];
    public ObservableCollection<SlowStarterRow> SlowStarters { get; } = [];

    [ObservableProperty] private string _latest = "—";
    [ObservableProperty] private string _average = "—";
    [ObservableProperty] private string _trend = "";
    [ObservableProperty] private string _latestBreakdown = "";

    protected override Task LoadAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            List<BootRecord> boots;
            List<BootDelay> delays;
            try { (boots, delays) = await Task.Run(() => BootEvents.Read()); }
            catch (EventLogNotFoundException)
            {
                StatusText = "Windows isn't recording boot times on this PC (its Diagnostics-Performance log is missing).";
                return;
            }
            Show(boots, delays);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(List<BootRecord> boots, List<BootDelay> delays)
    {
        Bars.Clear();
        SlowStarters.Clear();
        if (boots.Count == 0)
        {
            Latest = Average = "—";
            Trend = LatestBreakdown = "";
            StatusText = "No full start of Windows has been recorded yet. With Fast startup on, a shutdown is a hibernation; only restarts are measured.";
            return;
        }
        var shown = boots.TakeLast(30).ToList();
        double max = Math.Max(shown.Max(b => b.Total.TotalSeconds), 1);
        for (int i = 0; i < shown.Count; i++)
        {
            var b = shown[i];
            var local = b.TimeUtc.ToLocalTime();
            Bars.Add(new BootBar(local.ToString("d MMM"), b.Total.TotalSeconds, Math.Max(3, ChartHeight * b.Total.TotalSeconds / max),
                                 $"{local:g}: {b.Total.TotalSeconds:0.0} s (desktop ready after {b.MainPath.TotalSeconds:0.0} s)", i == shown.Count - 1));
        }
        var last = boots[^1];
        Latest = $"{last.Total.TotalSeconds:0} s";
        LatestBreakdown = $"Desktop after {last.MainPath.TotalSeconds:0} s, settled {last.PostBoot.TotalSeconds:0} s later · {last.TimeUtc.ToLocalTime():g}";
        Average = $"{shown.Average(b => b.Total.TotalSeconds):0} s";
        Trend = BootHistory.Trend(boots) switch
        {
            1 => "Starting up has got slower lately.",
            -1 => "Starting up has got faster lately.",
            _ => boots.Count >= 6 ? "Start-up time is steady." : "",
        };

        var since = boots.TakeLast(30).First().TimeUtc.AddMinutes(-10);
        foreach (var s in BootHistory.SlowStarters(delays.Where(d => d.TimeUtc >= since)).Take(15)) SlowStarters.Add(new SlowStarterRow(s));
        StatusText = SlowStarters.Count == 0
            ? $"{Format.Plural(boots.Count, "start")} recorded. Nothing has been reported as slowing it down."
            : $"{Format.Plural(boots.Count, "start")} recorded. Startup apps that slow it down can be switched off on the Startup programs page.";
    }

    [RelayCommand]
    private void OpenStartupPrograms() => navigate(typeof(AutorunViewModel));
}
