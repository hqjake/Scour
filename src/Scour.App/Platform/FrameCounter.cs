using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Scour.Core.Gaming;

namespace Scour.App.Platform;

/// <summary>
/// Counts the frames games present, from the graphics stack's own trace events (DXGI covers Direct3D 10-12
/// and most Vulkan/OpenGL games through their swap chains; D3D9 the older ones) — the same source PresentMon
/// uses. Needs admin rights, which Scour has.
/// </summary>
public sealed class FrameCounter : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, D3D9PresentStart = 1;

    private TraceEventSession? _session;

    public FrameRate Frames { get; } = new();
    public string? Error { get; private set; }
    public bool IsRunning => _session is not null;

    public void Start()
    {
        if (_session is not null) return;
        try
        {
            _session = new TraceEventSession("Scour-Frames") { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational);
            var source = _session.Source;
            source.AllEvents += e =>
            {
                int id = (int)e.ID;
                if (e.ProviderGuid == Dxgi && id == DxgiPresentStart || e.ProviderGuid == D3D9 && id == D3D9PresentStart)
                    Frames.Add(e.ProcessID, e.TimeStamp.ToUniversalTime().Ticks);
            };
            var thread = new Thread(() =>
            {
                try { source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "Scour frame counter" };
            thread.Start();
        }
        catch (Exception e)
        {
            Error = e.Message;
            Dispose();
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* already stopped */ }
        _session = null;
    }
}
