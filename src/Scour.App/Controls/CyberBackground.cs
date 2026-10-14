using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Scour.App.Controls;

/// <summary>
/// Slowly drifting aurora glows over a faint tech grid. Only transforms are animated, which
/// WPF composes on the GPU, so it costs almost nothing while idle.
/// </summary>
public sealed class CyberBackground : Grid
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly List<(Ellipse Shape, TranslateTransform Move, double X, double Y, double Size)> _blobs = [];

    public CyberBackground()
    {
        IsHitTestVisible = false;
        SetResourceReference(BackgroundProperty, "Scour.BackgroundBrush");
        Children.Add(_canvas);

        AddBlob("Scour.Blob1Color", 0.12, 0.10, 0.75, 34, 160, 90);
        AddBlob("Scour.Blob2Color", 0.85, 0.75, 0.70, 41, -140, -110);
        AddBlob("Scour.Blob3Color", 0.55, 0.35, 0.55, 27, 120, 140);

        Children.Add(new Border { Background = GridBrush() });

        // Vignette pulls the eye to the content.
        var vignette = new RadialGradientBrush { RadiusX = 0.9, RadiusY = 0.9 };
        vignette.GradientStops.Add(new GradientStop(Colors.Transparent, 0.55));
        vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 1));
        Children.Add(new Border { Background = vignette, Opacity = 0.8 });

        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) => Animate();
    }

    private Brush GridBrush()
    {
        var drawing = new GeometryDrawing
        {
            Geometry = Geometry.Parse("M 0,0 L 48,0 M 0,0 L 0,48"),
        };
        var pen = new Pen { Thickness = 1 };
        drawing.Pen = pen;
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 48, 48),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 48, 48),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        // Pen brush follows the theme.
        void Update() => pen.Brush = (TryFindResource("Scour.GridBrush") as Brush) ?? Brushes.Transparent;
        Update();
        Services.ThemeService.ThemeChanged += Update;
        return brush;
    }

    private void AddBlob(string colorKey, double x, double y, double size, double seconds, double dx, double dy)
    {
        var move = new TranslateTransform();
        var ellipse = new Ellipse { RenderTransform = move, Tag = (seconds, dx, dy) };
        void Paint()
        {
            var c = TryFindResource(colorKey) is Color col ? col : Colors.Transparent;
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(c, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(c.A / 3), c.R, c.G, c.B), 0.45));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            brush.Freeze();
            ellipse.Fill = brush;
        }
        Paint();
        Services.ThemeService.ThemeChanged += Paint;
        _canvas.Children.Add(ellipse);
        _blobs.Add((ellipse, move, x, y, size));
    }

    private void Layout()
    {
        double w = ActualWidth, h = ActualHeight, basis = Math.Max(w, h);
        foreach (var (shape, _, x, y, size) in _blobs)
        {
            shape.Width = shape.Height = basis * size;
            Canvas.SetLeft(shape, w * x - shape.Width / 2);
            Canvas.SetTop(shape, h * y - shape.Height / 2);
        }
    }

    private void Animate()
    {
        foreach (var (shape, move, _, _, _) in _blobs)
        {
            var (seconds, dx, dy) = ((double, double, double))shape.Tag;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.3))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        }
    }
}
