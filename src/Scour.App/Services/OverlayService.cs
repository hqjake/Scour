using System.Runtime.InteropServices;
using System.Windows.Threading;
using Scour.App.Platform;
using Scour.App.Views;

namespace Scour.App.Services;

/// <summary>Runs the in-game overlay: frame counting, sensor sampling and the window, only while it's shown.</summary>
public sealed class OverlayService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private OverlayWindow? _window;
    private FrameCounter? _frames;
    private SensorReader? _sensors;
    private bool _sampling;

    public OverlayService() => _timer.Tick += (_, _) => UpdateAsync().Forget("Updating the overlay", report: false);

    public bool IsVisible => _window is not null;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>Raised when the overlay is shown or hidden.</summary>
    public event Action<bool>? Changed;

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void Show()
    {
        if (_window is not null) return;
        _frames = new FrameCounter();
        _frames.Start();
        _window = new OverlayWindow();
        _window.Update("FPS —", "Starting…");
        _window.Show();
        _window.Place(Corner);
        _timer.Start();
        UpdateAsync().Forget("Updating the overlay", report: false);
        Changed?.Invoke(true);
    }

    public void Hide()
    {
        if (_window is null) return;
        _timer.Stop();
        _window.Close();
        _window = null;
        _frames?.Dispose();
        _frames = null;
        // The sensor reader stays: a sample may be running on another thread right now, and it's cheap to keep.
        Changed?.Invoke(false);
    }

    private async Task UpdateAsync()
    {
        if (_sampling || _window is null) return;
        _sampling = true;
        try
        {
            _sensors ??= await Task.Run(() => new SensorReader());
            var sensors = _sensors;
            var s = await Task.Run(() => sensors.Sample(includeDrives: false));
            if (_window is null || _frames is null) return;

            long now = DateTime.UtcNow.Ticks;
            int? pid = ForegroundPid();
            double? fps = pid is { } p ? _frames.Frames.Rate(p, now) : null;
            fps ??= _frames.Frames.Busiest(now) is { } busiest ? _frames.Frames.Rate(busiest, now) : null;

            var gpu = s.Gpus.OrderByDescending(g => g.UsagePercent ?? 0).FirstOrDefault();
            var parts = new List<string> { $"CPU {s.CpuPercent:0}%" };
            if (gpu is not null)
                parts.Add($"GPU {(gpu.UsagePercent is { } u ? $"{u:0}%" : "—")}" + (gpu.TemperatureC is { } t ? $" {t:0}°C" : ""));
            parts.Add($"RAM {s.MemoryUsed / (double)(1L << 30):0.0}/{s.MemoryTotal / (double)(1L << 30):0} GB");
            string fpsText = fps is { } f ? $"{f:0} FPS" : _frames.Error is null ? "FPS —" : "FPS n/a";
            _window.Update(fpsText, string.Join("   ", parts));
            _window.Place(Corner); // the text width changes
        }
        finally
        {
            _sampling = false;
        }
    }

    private static int? ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 || pid == Environment.ProcessId ? null : (int)pid;
    }

    public void Dispose()
    {
        Hide();
        if (!_sampling) _sensors?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
