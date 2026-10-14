namespace Scour.Core.DiskUsage;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>Squarified treemap layout (Bruls, Huizing &amp; van Wijk) — keeps tiles close to square.</summary>
public static class Treemap
{
    /// <param name="sizes">Item weights, largest first for best results. Zero/negative weights get an empty rect.</param>
    public static TreemapRect[] Layout(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        var result = new TreemapRect[sizes.Count];
        double total = sizes.Where(s => s > 0).Sum();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        double scale = bounds.Area / total;
        var items = Enumerable.Range(0, sizes.Count).Where(i => sizes[i] > 0).Select(i => (Index: i, Area: sizes[i] * scale)).ToList();
        var free = bounds;
        int start = 0;
        while (start < items.Count)
        {
            double side = Math.Min(free.Width, free.Height);
            int end = start + 1;
            double rowArea = items[start].Area;
            double worst = Worst(items, start, end, rowArea, side);
            while (end < items.Count)
            {
                double nextArea = rowArea + items[end].Area;
                double nextWorst = Worst(items, start, end + 1, nextArea, side);
                if (nextWorst > worst) break;
                rowArea = nextArea;
                worst = nextWorst;
                end++;
            }
            free = PlaceRow(items, start, end, rowArea, free, result);
            start = end;
        }
        return result;
    }

    private static double Worst(List<(int Index, double Area)> items, int start, int end, double rowArea, double side)
    {
        double max = 0, min = double.MaxValue;
        for (int i = start; i < end; i++)
        {
            max = Math.Max(max, items[i].Area);
            min = Math.Min(min, items[i].Area);
        }
        double s2 = side * side, r2 = rowArea * rowArea;
        return Math.Max(s2 * max / r2, r2 / (s2 * min));
    }

    private static TreemapRect PlaceRow(List<(int Index, double Area)> items, int start, int end, double rowArea, TreemapRect free, TreemapRect[] result)
    {
        bool horizontal = free.Width >= free.Height; // lay the row along the short side
        if (horizontal)
        {
            double w = rowArea / free.Height;
            double y = free.Y;
            for (int i = start; i < end; i++)
            {
                double h = items[i].Area / w;
                result[items[i].Index] = new TreemapRect(free.X, y, w, h);
                y += h;
            }
            return new TreemapRect(free.X + w, free.Y, Math.Max(0, free.Width - w), free.Height);
        }
        else
        {
            double h = rowArea / free.Width;
            double x = free.X;
            for (int i = start; i < end; i++)
            {
                double w = items[i].Area / h;
                result[items[i].Index] = new TreemapRect(x, free.Y, w, h);
                x += w;
            }
            return new TreemapRect(free.X, free.Y + h, free.Width, Math.Max(0, free.Height - h));
        }
    }
}
