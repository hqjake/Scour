using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Scour.App.Controls;

/// <summary>Circular percentage gauge with a glowing gradient arc that animates to new values.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new PropertyMetadata(0.0, OnValueChanged));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public RingGauge()
    {
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55, Color = Color.FromRgb(139, 92, 246) };
        Services.ThemeService.ThemeChanged += InvalidateVisual;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RingGauge)d;
        g.BeginAnimation(DisplayValueProperty, new DoubleAnimation(Math.Clamp((double)e.NewValue, 0, 100), TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2 - 2;

        var track = (TryFindResource("Scour.SubtleBrush") as Brush) ?? Brushes.Gray;
        dc.DrawEllipse(null, new Pen(track, Thickness), center, r, r);

        double v = (double)GetValue(DisplayValueProperty);
        if (v <= 0.1) return;
        double angle = Math.Min(v, 99.9) / 100 * 360;
        var start = new Point(center.X, center.Y - r);
        double rad = (angle - 90) * Math.PI / 180;
        var end = new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        var brush = (TryFindResource("Scour.AccentGradient") as Brush) ?? Brushes.MediumPurple;
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, geo);
    }
}
