using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scour.Core.DiskUsage;

namespace Scour.App.Controls;

/// <summary>
/// Draws a disk tree as nested, cushion-shaded tiles coloured by file type. Click selects,
/// double-click on a folder zooms into it.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(DiskNode), typeof(TreemapControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(DiskNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(TreemapControl), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public DiskNode? Root { get => (DiskNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public DiskNode? Selected { get => (DiskNode?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>Bump to force a redraw after the tree changed (e.g. a deletion).</summary>
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    public event Action<DiskNode>? ZoomRequested;

    private readonly List<(Rect Rect, DiskNode Node)> _hits = [];
    private DiskNode? _hover;
    private const double MinTile = 3;
    private const int MaxDepth = 6;

    public TreemapControl()
    {
        ClipToBounds = true;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTip = "";
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (Root is null || Root.Size <= 0 || bounds.Width < 4 || bounds.Height < 4) return;
        Draw(dc, Root, bounds, 0);

        if (Selected is not null)
        {
            var hit = _hits.FirstOrDefault(h => ReferenceEquals(h.Node, Selected));
            if (hit.Node is not null)
            {
                var glow = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF3, 0xFF)), 2);
                dc.DrawRectangle(null, glow, Inflate(hit.Rect, -1));
            }
        }
    }

    private void Draw(DrawingContext dc, DiskNode node, Rect r, int depth)
    {
        if (node.IsDirectory && node.Children is { Count: > 0 } kids && depth < MaxDepth)
        {
            // Folder: thin frame, then its children inside.
            var inner = depth == 0 ? r : Inflate(r, -1.5);
            if (depth > 0)
            {
                dc.DrawRectangle(FolderFill, FolderEdge, r);
                _hits.Add((r, node));
            }
            var visible = kids.Where(k => k.Size > 0).ToList();
            var rects = Treemap.Layout(visible.Select(k => (double)k.Size).ToArray(),
                                       new TreemapRect(inner.X, inner.Y, inner.Width, inner.Height));
            for (int i = 0; i < visible.Count; i++)
            {
                var t = rects[i];
                if (t.Width < MinTile || t.Height < MinTile) continue;
                Draw(dc, visible[i], new Rect(t.X, t.Y, t.Width, t.Height), depth + 1);
            }
            return;
        }

        // File (or folder too deep to subdivide): cushion-shaded tile.
        var baseColor = node.IsDirectory ? Color.FromRgb(109, 40, 217) : ColorFor(node.Extension);
        dc.DrawRectangle(Cushion(baseColor), TileEdge, r);
        if (ReferenceEquals(node, _hover))
            dc.DrawRectangle(HoverFill, null, r);
        _hits.Add((r, node));

        if (r.Width > 70 && r.Height > 18)
        {
            var text = new FormattedText(node.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
                                         Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = r.Width - 6, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(r.X + 3, r.Y + 2));
        }
    }

    private static Rect Inflate(Rect r, double d) =>
        r.Width + 2 * d <= 0 || r.Height + 2 * d <= 0 ? r : new Rect(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    /// <summary>Deepest node under the point.</summary>
    private DiskNode? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            ToolTip = hit is null ? null : $"{hit.FullPath}\n{Services.Format.Bytes(hit.Size)}" + (hit.IsDirectory ? $" · {hit.FileCount:N0} files" : "");
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit is null) return;
        if (e.ClickCount == 2)
        {
            var target = hit.IsDirectory ? hit : hit.Parent;
            if (target is not null) ZoomRequested?.Invoke(target);
        }
        else
        {
            Selected = hit;
        }
    }

    // ---- colours (brushes are cached and frozen: a full redraw can paint tens of thousands of tiles)

    private static readonly Dictionary<Color, Brush> Cushions = new();
    private static readonly Pen TileEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 0.5));
    private static readonly Pen FolderEdge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(90, 167, 139, 250)), 1));
    private static readonly Brush FolderFill = Frozen(new SolidColorBrush(Color.FromArgb(40, 139, 92, 246)));
    private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Brush Cushion(Color c)
    {
        if (!Cushions.TryGetValue(c, out var b))
            Cushions[c] = b = Frozen(new LinearGradientBrush(Lighten(c, 0.35), Darken(c, 0.35), new Point(0, 0), new Point(1, 1)));
        return b;
    }

    private static readonly Dictionary<string, Color> Categories = BuildCategories();

    private static Dictionary<string, Color> BuildCategories()
    {
        var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        void Add(Color c, params string[] exts) { foreach (var e in exts) map[e] = c; }
        Add(Color.FromRgb(232, 121, 249), ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".ts");
        Add(Color.FromRgb(34, 211, 238), ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".raw", ".psd", ".tif", ".tiff", ".svg");
        Add(Color.FromRgb(251, 191, 36), ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma");
        Add(Color.FromRgb(251, 146, 60), ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".cab", ".msi", ".vhd", ".vhdx", ".wim", ".esd");
        Add(Color.FromRgb(248, 113, 113), ".exe", ".dll", ".sys", ".ocx", ".msu");
        Add(Color.FromRgb(52, 211, 153), ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".odt");
        Add(Color.FromRgb(96, 165, 250), ".pak", ".bin", ".dat", ".db", ".sqlite", ".bundle", ".assets", ".uasset", ".vpk");
        return map;
    }

    public static Color ColorFor(string extension)
    {
        if (Categories.TryGetValue(extension, out var c)) return c;
        // Stable violet-family colour for everything else.
        int h = 0;
        foreach (var ch in extension) h = h * 31 + ch;
        double hue = 250 + (Math.Abs(h) % 70); // 250°–320°: violet → magenta
        return FromHsl(hue, 0.55, 0.55);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static Color Lighten(Color c, double f) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    private static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));
}
