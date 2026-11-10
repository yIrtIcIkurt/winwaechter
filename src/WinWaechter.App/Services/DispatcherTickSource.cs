using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WinWaechter.App.ViewModels;

namespace WinWaechter.App.Services;

/// <summary>ITickSource on the UI thread's dispatcher. While the main window is minimized the timer is paused
/// and resumes on restore, so nothing is sampled that nobody can see.</summary>
public sealed class DispatcherTickSource : ITickSource
{
    // WindowState follows the real window (WPF updates it on minimize/restore), and unlike StateChanged the
    // property notification also arrives for a window that was never shown
    static readonly DependencyPropertyDescriptor StateProperty =
        DependencyPropertyDescriptor.FromProperty(Window.WindowStateProperty, typeof(Window));

    readonly Func<Window?> _window;
    DispatcherTimer? _timer;
    Window? _watched;

    public DispatcherTickSource(Func<Window?>? window = null) =>
        _window = window ?? (() => Application.Current?.MainWindow);

    /// <summary>True while ticks are delivered (started and the window not minimized).</summary>
    public bool IsTicking => _timer?.IsEnabled == true;

    public void Start(TimeSpan every, Action tick)
    {
        Stop();
        var timer = new DispatcherTimer { Interval = every };
        timer.Tick += (_, _) =>
        {
            if (IsMinimized()) { timer.Stop(); return; }
            tick();
        };
        _timer = timer;
        _watched = _window();
        if (_watched is not null) StateProperty.AddValueChanged(_watched, OnStateChanged);
        if (!IsMinimized()) timer.Start();
    }

    public void Stop()
    {
        if (_watched is not null) StateProperty.RemoveValueChanged(_watched, OnStateChanged);
        _watched = null;
        _timer?.Stop();
        _timer = null;
    }

    bool IsMinimized() => _watched?.WindowState == WindowState.Minimized;

    void OnStateChanged(object? sender, EventArgs e)
    {
        if (_timer is null) return;
        if (IsMinimized()) _timer.Stop();
        else if (!_timer.IsEnabled) _timer.Start();
    }
}
