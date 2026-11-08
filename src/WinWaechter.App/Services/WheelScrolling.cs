using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WinWaechter.App.Services;

/// <summary>
/// Makes mouse-wheel and touchpad scrolling proportional to the actual wheel delta.
/// WPF's ScrollViewer scrolls a fixed step per wheel event, so precision touchpads
/// (many small events) race through lists. Here one wheel notch (120) = 48 px, like
/// WPF's default for a mouse, and smaller touchpad deltas scroll proportionally less.
/// </summary>
public static class WheelScrolling
{
    /// <summary>Pixels per wheel notch (delta 120).</summary>
    public const double PixelsPerNotch = 48;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnMouseWheel));

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || sv.CanContentScroll) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;   // keep zoom/horizontal gestures

        double target = NextOffset(sv.VerticalOffset, sv.ScrollableHeight, e.Delta);
        if (target == sv.VerticalOffset) return;               // at the edge: let an outer viewer scroll
        sv.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    /// <summary>New vertical offset for a wheel delta, clamped to the scrollable range.</summary>
    public static double NextOffset(double offset, double scrollableHeight, int delta)
    {
        if (scrollableHeight <= 0) return offset;
        double next = offset - delta / 120.0 * PixelsPerNotch;
        return Math.Clamp(next, 0, scrollableHeight);
    }
}
