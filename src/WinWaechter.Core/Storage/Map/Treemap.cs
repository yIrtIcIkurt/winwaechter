namespace WinWaechter.Core.Storage.Map;

public readonly record struct Rect01(double X, double Y, double W, double H);

/// <summary>Squarified treemap (Bruls, Huizing, van Wijk 2000): areas proportional to size, tiles as square as possible.</summary>
public static class Treemap
{
    public static IReadOnlyList<(int Index, Rect01 Box)> Squarify(IReadOnlyList<long> sizes, double width, double height)
    {
        var items = sizes.Select((s, i) => (Index: i, Size: s)).Where(x => x.Size > 0).OrderByDescending(x => x.Size).ToList();
        var result = new List<(int, Rect01)>();
        if (items.Count == 0 || width <= 0 || height <= 0) return result;
        var total = (double)items.Sum(x => x.Size);
        var scale = width * height / total;
        var areas = items.Select(x => (x.Index, Area: x.Size * scale)).ToList();

        double x0 = 0, y0 = 0, w = width, h = height;
        var row = new List<(int Index, double Area)>();
        foreach (var a in areas)
        {
            var side = Math.Min(w, h);
            if (row.Count == 0 || Worst([.. row, a], side) <= Worst(row, side))
            {
                row.Add(a);
                continue;
            }
            Layout(row);
            row = [a];
        }
        if (row.Count > 0) Layout(row);
        return result;

        void Layout(List<(int Index, double Area)> r)
        {
            var sum = r.Sum(t => t.Area);
            if (w >= h)
            {
                // a column on the left
                var colW = sum / h;
                var y = y0;
                foreach (var (index, area) in r)
                {
                    var itemH = area / colW;
                    result.Add((index, new Rect01(x0, y, colW, itemH)));
                    y += itemH;
                }
                x0 += colW;
                w -= colW;
            }
            else
            {
                // a row at the top
                var rowH = sum / w;
                var x = x0;
                foreach (var (index, area) in r)
                {
                    var itemW = area / rowH;
                    result.Add((index, new Rect01(x, y0, itemW, rowH)));
                    x += itemW;
                }
                y0 += rowH;
                h -= rowH;
            }
        }
    }

    // worst aspect ratio of a row laid along a side of the given length
    static double Worst(List<(int Index, double Area)> row, double side)
    {
        var sum = row.Sum(t => t.Area);
        var max = row.Max(t => t.Area);
        var min = row.Min(t => t.Area);
        var s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
    }
}
