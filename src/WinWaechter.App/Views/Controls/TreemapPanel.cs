using System.Windows;
using System.Windows.Controls;
using WinWaechter.Core.Storage.Map;

namespace WinWaechter.App.Views.Controls;

/// <summary>
/// Lays its children out as a squarified treemap: each child's area is proportional to its attached
/// <see cref="SizeProperty"/>. Children keep their order for keyboard focus; only their position follows size.
/// </summary>
public sealed class TreemapPanel : Panel
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached("Size", typeof(double), typeof(TreemapPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetSize(UIElement e) => (double)e.GetValue(SizeProperty);
    public static void SetSize(UIElement e, double value) => e.SetValue(SizeProperty, value);

    // a container (ContentPresenter) carries the size of the item it shows
    static double SizeOf(UIElement e) =>
        GetSize(e) is var s and > 0 ? s
        : e is FrameworkElement { DataContext: { } dc } && dc.GetType().GetProperty("Bytes")?.GetValue(dc) is long b ? b
        : 0;

    IReadOnlyList<(int Index, Rect01 Box)> Layout(Size size)
    {
        var sizes = InternalChildren.Cast<UIElement>().Select(c => (long)Math.Round(SizeOf(c))).ToList();
        return Treemap.Squarify(sizes, size.Width, size.Height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 280 : availableSize.Height);
        var boxes = Layout(size).ToDictionary(b => b.Index, b => b.Box);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(boxes.TryGetValue(i, out var r) ? new Size(r.W, r.H) : new Size(0, 0));
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var boxes = Layout(finalSize).ToDictionary(b => b.Index, b => b.Box);
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (boxes.TryGetValue(i, out var r)) child.Arrange(new Rect(r.X, r.Y, r.W, r.H));
            else child.Arrange(new Rect(0, 0, 0, 0));   // zero-size entries get no tile
        }
        return finalSize;
    }
}
