using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinWaechter.App.Services;

/// <summary>
/// Makes mouse-wheel and touchpad scrolling proportional to the actual wheel delta.
/// WPF's ScrollViewer scrolls a fixed step per wheel event, so precision touchpads
/// (many small events) race through lists. Here one wheel notch (120) = 48 px, like
/// WPF's default for a mouse, and smaller touchpad deltas scroll proportionally less.
/// Also lets the arrow keys, Page Up/Down, Home/End and Space scroll the page.
/// </summary>
public static class WheelScrolling
{
    /// <summary>Pixels per wheel notch (delta 120).</summary>
    public const double PixelsPerNotch = 48;

    /// <summary>Pixels per arrow key press.</summary>
    public const double PixelsPerArrow = 40;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnMouseWheel));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent,
            new KeyEventHandler(OnKeyDown));
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (sv.CanContentScroll && !ScrollsInPixels(sv)) return;   // item-based scrolling: offsets are not pixels
        if (Keyboard.Modifiers != ModifierKeys.None) return;      // keep zoom/horizontal gestures

        double target = NextOffset(sv.VerticalOffset, sv.ScrollableHeight, e.Delta);
        if (target == sv.VerticalOffset) return;                  // at the edge: let an outer viewer scroll
        sv.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    /// <summary>A virtualized list with ScrollUnit=Pixel reports its offsets in pixels as well.</summary>
    static bool ScrollsInPixels(ScrollViewer sv) =>
        sv.TemplatedParent is ItemsControl ic
        && VirtualizingPanel.GetIsVirtualizing(ic)
        && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel;

    /// <summary>New vertical offset for a wheel delta, clamped to the scrollable range.</summary>
    public static double NextOffset(double offset, double scrollableHeight, int delta)
    {
        if (scrollableHeight <= 0) return offset;
        double next = offset - delta / 120.0 * PixelsPerNotch;
        return Math.Clamp(next, 0, scrollableHeight);
    }

    /// <summary>New vertical offset for a scroll key, or null when the key does not scroll.</summary>
    public static double? KeyOffset(Key key, double offset, double scrollableHeight, double viewportHeight)
    {
        if (scrollableHeight <= 0) return null;
        double page = Math.Max(PixelsPerArrow, viewportHeight * 0.9);
        double? next = key switch
        {
            Key.Up => offset - PixelsPerArrow,
            Key.Down => offset + PixelsPerArrow,
            Key.PageUp => offset - page,
            Key.PageDown or Key.Space => offset + page,
            Key.Home => 0,
            Key.End => scrollableHeight,
            _ => null,
        };
        return next is { } n ? Math.Clamp(n, 0, scrollableHeight) : null;
    }

    // Runs when no control handled the key itself (text boxes, combo boxes, buttons keep their keys).
    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not Window w) return;
        if (Keyboard.FocusedElement is DependencyObject f && KeepsKeys(f, e.Key)) return;

        foreach (var sv in Candidates(w))
        {
            if (sv.CanContentScroll && !ScrollsInPixels(sv)) continue;
            var target = KeyOffset(e.Key, sv.VerticalOffset, sv.ScrollableHeight, sv.ViewportHeight);
            if (target is null) return;                 // not a scroll key
            if (target.Value == sv.VerticalOffset) continue;   // at the edge: try the outer viewer
            sv.ScrollToVerticalOffset(target.Value);
            e.Handled = true;
            return;
        }
    }

    /// <summary>Text input and selection controls use these keys themselves.</summary>
    static bool KeepsKeys(DependencyObject focused, Key key)
    {
        for (var d = focused; d is not null; d = Parent(d))
        {
            if (d is TextBoxBase or PasswordBox or ComboBox or Slider) return true;
            if (key == Key.Space && d is ButtonBase) return true;
        }
        return false;
    }

    /// <summary>Viewers to try, innermost first: around the focused element, under the mouse, then the largest one.</summary>
    static IEnumerable<ScrollViewer> Candidates(Window w)
    {
        var seen = new HashSet<ScrollViewer>();
        foreach (var start in new[] { Keyboard.FocusedElement as DependencyObject, Mouse.DirectlyOver as DependencyObject })
        {
            if (start is null || start == w) continue;
            for (var d = start; d is not null; d = Parent(d))
                if (d is ScrollViewer sv && Usable(sv) && seen.Add(sv)) yield return sv;
        }
        var largest = All(w).Where(Usable).OrderByDescending(sv => sv.ViewportHeight * sv.ViewportWidth).FirstOrDefault();
        if (largest is not null && seen.Add(largest))
        {
            yield return largest;
            for (var d = Parent(largest); d is not null; d = Parent(d))
                if (d is ScrollViewer sv && Usable(sv) && seen.Add(sv)) yield return sv;
        }
    }

    // Viewers inside text boxes belong to the text box; hidden viewers are skipped.
    static bool Usable(ScrollViewer sv) =>
        sv.IsVisible && sv.ScrollableHeight > 0 && sv.TemplatedParent is not TextBoxBase;

    static IEnumerable<ScrollViewer> All(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) yield return sv;
            foreach (var inner in All(c)) yield return inner;
        }
    }

    static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
        : LogicalTreeHelper.GetParent(d);
}
