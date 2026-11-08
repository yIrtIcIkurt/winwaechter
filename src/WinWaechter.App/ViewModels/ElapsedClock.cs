using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinWaechter.App.ViewModels;

/// <summary>Shows how long an operation has been running (updated once a second).</summary>
public sealed partial class ElapsedClock : ObservableObject
{
    CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _text = "00:00";

    public void Start()
    {
        Stop();
        var cts = _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        Text = Labels.Elapsed(TimeSpan.Zero);
        _ = TickAsync(sw, cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    async Task TickAsync(Stopwatch sw, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                Text = Labels.Elapsed(sw.Elapsed);
            }
        }
        catch (OperationCanceledException) { }
    }
}
