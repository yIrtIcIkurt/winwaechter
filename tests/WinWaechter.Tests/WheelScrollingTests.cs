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
}
