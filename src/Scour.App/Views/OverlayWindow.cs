using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scour.App.Views;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// The in-game overlay: a small always-on-top panel that mouse clicks pass through and that never takes focus
/// from the game. Shows over games in windowed and borderless mode (most games' default); exclusive
/// full-screen hides everything else, including this.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBlock _fps = new() { FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) };
    private readonly TextBlock _details = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF0, 0xFF)), LineHeight = 17 };

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Scour overlay";
        IsHitTestVisible = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x07, 0x06, 0x0E)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = new StackPanel { Children = { _fps, _details } },
        };
    }

    /// <summary>Not part of the app's window list for theming (no title bar, no neon border).</summary>
    public static bool IsOverlay(Window w) => w is OverlayWindow;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED));
    }

    public void Update(string fps, string details)
    {
        _fps.Text = fps;
        _details.Text = details;
    }

    public void Place(OverlayCorner corner)
    {
        UpdateLayout();
        const double margin = 12;
        double w = ActualWidth, h = ActualHeight;
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Left = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? margin : sw - w - margin;
        Top = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? margin : sh - h - margin;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
