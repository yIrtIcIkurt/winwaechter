using System.Windows.Threading;
using WinWaechter.App.ViewModels;

namespace WinWaechter.App.Services;

/// <summary>ITickSource on the UI thread's dispatcher.</summary>
public sealed class DispatcherTickSource : ITickSource
{
    DispatcherTimer? _timer;

    public void Start(TimeSpan every, Action tick)
    {
        Stop();
        _timer = new DispatcherTimer { Interval = every };
        _timer.Tick += (_, _) => tick();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }
}
