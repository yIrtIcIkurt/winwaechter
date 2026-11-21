using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinWaechter.App.Views;

/// <summary>
/// A row of readouts with a thin line between them. When a column would get narrower than
/// <see cref="MinColumnWidth"/>, the readouts wrap into even rows (6 → 3 × 2 → 2 × 3 → 1 × 6) instead of being cut off.
/// </summary>
public sealed class ReadoutPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double),
        typeof(ReadoutPanel), new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(nameof(ColumnGap), typeof(double),
        typeof(ReadoutPanel), new FrameworkPropertyMetadata(36.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(nameof(RowGap), typeof(double),
        typeof(ReadoutPanel), new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush),
        typeof(ReadoutPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double ColumnGap { get => (double)GetValue(ColumnGapProperty); set => SetValue(ColumnGapProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    int _columns = 1;
    readonly List<double> _rowHeights = [];

    List<UIElement> Shown() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    // the most columns that keep every column wide enough and every row full
    int ColumnsFor(double width, int count)
    {
        if (count <= 1 || double.IsInfinity(width)) return Math.Max(count, 1);
        for (var c = count; c > 1; c--)
        {
            if (count % c != 0) continue;
            if ((width - ColumnGap * (c - 1)) / c >= MinColumnWidth) return c;
        }
        return 1;
    }

    double CellWidth(double width) => (width - ColumnGap * (_columns - 1)) / _columns;

    protected override Size MeasureOverride(Size available)
    {
        var shown = Shown();
        _columns = ColumnsFor(available.Width, shown.Count);
        var cell = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, CellWidth(available.Width));
        _rowHeights.Clear();
        double widest = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Measure(new Size(cell, double.PositiveInfinity));
            if (i % _columns == 0) _rowHeights.Add(0);
            _rowHeights[^1] = Math.Max(_rowHeights[^1], shown[i].DesiredSize.Height);
            widest = Math.Max(widest, shown[i].DesiredSize.Width);
        }
        var height = _rowHeights.Sum() + RowGap * Math.Max(0, _rowHeights.Count - 1);
        var width = double.IsInfinity(available.Width) ? widest * _columns + ColumnGap * (_columns - 1) : available.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var shown = Shown();
        var cell = Math.Max(0, CellWidth(final.Width));
        double y = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            var row = i / _columns;
            var col = i % _columns;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + RowGap;
            shown[i].Arrange(new Rect(col * (cell + ColumnGap), y, cell, row < _rowHeights.Count ? _rowHeights[row] : 0));
        }
        return final;
    }

    // a hairline between neighbours in each row (the instrument look of the old grid)
    protected override void OnRender(DrawingContext dc)
    {
        if (LineBrush is null || _columns < 2) return;
        var shown = Shown();
        var cell = CellWidth(ActualWidth);
        var pen = new Pen(LineBrush, 1);
        double y = 0;
        for (var row = 0; row < _rowHeights.Count; row++)
        {
            var inRow = Math.Min(_columns, shown.Count - row * _columns);
            for (var col = 1; col < inRow; col++)
            {
                var x = Math.Round(col * (cell + ColumnGap) - ColumnGap / 2) + 0.5;
                dc.DrawLine(pen, new Point(x, y), new Point(x, y + _rowHeights[row]));
            }
            y += _rowHeights[row] + RowGap;
        }
    }
}
