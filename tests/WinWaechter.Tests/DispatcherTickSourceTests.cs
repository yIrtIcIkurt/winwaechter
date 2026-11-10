using System.Windows;
using WinWaechter.App.Services;

namespace WinWaechter.Tests;

public sealed class DispatcherTickSourceTests
{
    static void OnSta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Pauses_while_the_window_is_minimized()
    {
        OnSta(() =>
        {
            // never shown: WindowState and StateChanged work without putting a window on the desktop
            var window = new Window();
            try
            {
                var ticks = new DispatcherTickSource(() => window);
                ticks.Start(TimeSpan.FromSeconds(2), () => { });
                Assert.True(ticks.IsTicking);

                window.WindowState = WindowState.Minimized;
                Assert.False(ticks.IsTicking);

                window.WindowState = WindowState.Normal;
                Assert.True(ticks.IsTicking);

                ticks.Stop();
                Assert.False(ticks.IsTicking);
                // a stopped source stays stopped when the window changes state
                window.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Normal;
                Assert.False(ticks.IsTicking);

                // started while minimized: nothing ticks until the window is restored
                window.WindowState = WindowState.Minimized;
                ticks.Start(TimeSpan.FromSeconds(2), () => { });
                Assert.False(ticks.IsTicking);
                window.WindowState = WindowState.Normal;
                Assert.True(ticks.IsTicking);
                ticks.Stop();
            }
            finally
            {
                window.Close();
            }
        });
    }
}
