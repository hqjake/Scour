using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Scour.App.Services;

/// <summary>Switches between the dark and light cyber themes at runtime.</summary>
public static class ThemeService
{
    // Index 0 = Fluent base theme, index 1 = Scour palette (see App.xaml).
    private const int FluentIndex = 0;
    private const int PaletteIndex = 1;

    public static bool IsDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;
        var mode = dark ? "Dark" : "Light";
        merged[FluentIndex] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{mode}.xaml"),
        };
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Scour;component/Themes/Cyber.{mode}.xaml") };
        merged[PaletteIndex] = palette;

        // Fluent may push the Windows accent colour into the top-level resources; ours must win.
        foreach (DictionaryEntry e in palette)
            if (e.Key is string k && (k.StartsWith("Accent") || k.StartsWith("SystemAccent")))
                app.Resources[k] = e.Value;

        ApplyImplicitStyles(app);
        foreach (Window w in app.Windows) StyleWindow(w);
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Translucent data surfaces so the animated background glows through. Built in code on top of
    /// whatever style the active Fluent theme defines (or the default one if it has none).
    /// </summary>
    private static void ApplyImplicitStyles(Application app)
    {
        Make(app, typeof(DataGrid),
            (Control.BackgroundProperty, "Scour.CardBrush"), (Control.BorderBrushProperty, "Scour.StrokeBrush"),
            (DataGrid.RowBackgroundProperty, Brushes.Transparent), (Control.BorderThicknessProperty, new Thickness(1)));
        MakeCellStyle(app);
        Make(app, typeof(ListView), (Control.BackgroundProperty, "Scour.CardBrush"), (Control.BorderBrushProperty, "Scour.StrokeBrush"));
        Make(app, typeof(ListBox), (Control.BackgroundProperty, "Scour.CardBrush"), (Control.BorderBrushProperty, "Scour.StrokeBrush"));
        Make(app, typeof(ProgressBar), (Control.ForegroundProperty, "Scour.AccentGradient"), (UIElement.EffectProperty, app.TryFindResource("SoftGlow")));
    }

    private static void MakeCellStyle(Application app)
    {
        app.Resources.Remove(typeof(DataGridCell));
        var style = new Style(typeof(DataGridCell), app.TryFindResource(typeof(DataGridCell)) as Style);
        if (app.TryFindResource("Scour.DataGridCellTemplate") is ControlTemplate template)
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Scour.SelectionBrush")));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Scour.TextBrush")));
        style.Triggers.Add(selected);
        app.Resources[typeof(DataGridCell)] = style;
    }

    private static void Make(Application app, Type type, params (DependencyProperty Property, object? Value)[] setters)
    {
        app.Resources.Remove(type);
        var style = new Style(type, app.TryFindResource(type) as Style);
        foreach (var (property, value) in setters)
        {
            if (value is null) continue;
            style.Setters.Add(value is string key ? new Setter(property, new DynamicResourceExtension(key)) : new Setter(property, value));
        }
        app.Resources[type] = style;
    }

    // ---- title bar / border colours (Windows 11)

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Colours the native title bar to match the theme and gives the window a neon border.</summary>
    public static void StyleWindow(Window window)
    {
        if (window is Views.OverlayWindow) return; // borderless and see-through by design
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
            return;
        }
        try
        {
            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int caption = ColorRef(Res<Color>("Scour.BgColor", IsDark ? Color.FromRgb(7, 6, 14) : Color.FromRgb(245, 243, 255)));
            int border = ColorRef(Res<Color>("Scour.AccentColor", Color.FromRgb(139, 92, 246)));
            int text = ColorRef(IsDark ? Color.FromRgb(243, 240, 255) : Color.FromRgb(26, 20, 51));
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch { /* older Windows: attributes not supported */ }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window w) StyleWindow(w);
    }

    private static T Res<T>(string key, T fallback) => Application.Current.TryFindResource(key) is T v ? v : fallback;

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
