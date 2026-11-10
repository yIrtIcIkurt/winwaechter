using WinWaechter.App.Services;

namespace WinWaechter.Tests;

public class WheelScrollingTests
{
    [Fact]
    public void One_notch_scrolls_48_pixels()
    {
        Assert.Equal(148, WheelScrolling.NextOffset(100, 1000, -120));
        Assert.Equal(52, WheelScrolling.NextOffset(100, 1000, 120));
    }

    [Fact]
    public void Small_touchpad_delta_scrolls_proportionally_less()
    {
        Assert.Equal(104, WheelScrolling.NextOffset(100, 1000, -10));
    }

    [Fact]
    public void Offset_is_clamped_to_the_scrollable_range()
    {
        Assert.Equal(0, WheelScrolling.NextOffset(10, 1000, 240));
        Assert.Equal(1000, WheelScrolling.NextOffset(990, 1000, -240));
        Assert.Equal(5, WheelScrolling.NextOffset(5, 0, -120));
    }

    [Fact]
    public void Scroll_keys_move_by_line_page_or_to_the_ends()
    {
        Assert.Equal(140, WheelScrolling.KeyOffset(System.Windows.Input.Key.Down, 100, 1000, 500));
        Assert.Equal(60, WheelScrolling.KeyOffset(System.Windows.Input.Key.Up, 100, 1000, 500));
        Assert.Equal(550, WheelScrolling.KeyOffset(System.Windows.Input.Key.PageDown, 100, 1000, 500));
        Assert.Equal(550, WheelScrolling.KeyOffset(System.Windows.Input.Key.Space, 100, 1000, 500));
        Assert.Equal(0, WheelScrolling.KeyOffset(System.Windows.Input.Key.PageUp, 100, 1000, 500));
        Assert.Equal(0, WheelScrolling.KeyOffset(System.Windows.Input.Key.Home, 100, 1000, 500));
        Assert.Equal(1000, WheelScrolling.KeyOffset(System.Windows.Input.Key.End, 100, 1000, 500));
        Assert.Equal(1000, WheelScrolling.KeyOffset(System.Windows.Input.Key.Down, 990, 1000, 500));
    }

    [Fact]
    public void Other_keys_or_nothing_to_scroll_give_null()
    {
        Assert.Null(WheelScrolling.KeyOffset(System.Windows.Input.Key.A, 100, 1000, 500));
        Assert.Null(WheelScrolling.KeyOffset(System.Windows.Input.Key.Enter, 100, 1000, 500));
        Assert.Null(WheelScrolling.KeyOffset(System.Windows.Input.Key.Down, 0, 0, 500));
    }
}
