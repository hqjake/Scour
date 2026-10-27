using System.Windows.Interop;

namespace Scour.App.Platform;

/// <summary>
/// System-wide keyboard shortcuts (they work inside full-screen games). A hidden message-only window receives
/// them, so they work while Scour sits in the tray without ever having shown its window.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public const string GameModeShortcut = "Ctrl+Alt+G";
    public const string OverlayShortcut = "Ctrl+Alt+O";
    public const uint Alt = 0x1, Control = 0x2, NoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5300;

    public GlobalHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("ScourHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }); // HWND_MESSAGE
        _window.AddHook(Hook);
    }

    /// <summary>Registers Ctrl+Alt+<paramref name="key"/>; false if another program already owns it.</summary>
    public bool Register(char key, Action action)
    {
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, Control | Alt | NoRepeat, char.ToUpperInvariant(key))) return false;
        _actions[id] = action;
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
        _window.Dispose();
    }
}
