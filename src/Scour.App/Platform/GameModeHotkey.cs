using System.Windows.Interop;

namespace Scour.App.Platform;

/// <summary>
/// Ctrl+Alt+G switches Game mode from anywhere, including from inside a full-screen game. Uses a hidden
/// message-only window, so it works while Scour sits in the tray without ever having shown its window.
/// </summary>
public sealed class GameModeHotkey : IDisposable
{
    public const string Description = "Ctrl+Alt+G";
    private const int Id = 0x5343; // "SC"
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkG = 0x47;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _window;
    private readonly Action _pressed;

    /// <summary>False if another program already owns the shortcut.</summary>
    public bool IsRegistered { get; }

    public GameModeHotkey(Action pressed)
    {
        _pressed = pressed;
        _window = new HwndSource(new HwndSourceParameters("ScourHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }); // HWND_MESSAGE
        _window.AddHook(Hook);
        IsRegistered = NativeMethods.RegisterHotKey(_window.Handle, Id, ModControl | ModAlt | ModNoRepeat, VkG);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (IsRegistered) NativeMethods.UnregisterHotKey(_window.Handle, Id);
        _window.Dispose();
    }
}
