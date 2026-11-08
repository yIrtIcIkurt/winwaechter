using CommunityToolkit.Mvvm.ComponentModel;

namespace WinWaechter.App.Services;

/// <summary>
/// App-wide lock for system changes (apply, undo, clean, kill, purge …), so two pages never change
/// the system or the session store at the same time.
/// </summary>
public sealed partial class ChangeGate : ObservableObject
{
    public const string BusyHint = "Eine andere Änderung läuft gerade.";

    readonly SemaphoreSlim _lock = new(1, 1);

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The view model that currently holds the gate (null when free).</summary>
    [ObservableProperty]
    private object? _owner;

    /// <summary>True when the gate is held by someone other than <paramref name="me"/>.</summary>
    public bool IsBusyFor(object me) => IsBusy && !ReferenceEquals(Owner, me);

    /// <summary>Runs action while holding the gate; returns false (and runs nothing) when it is taken.</summary>
    public async Task<bool> RunAsync(object owner, Func<Task> action)
    {
        if (!_lock.Wait(0)) return false;
        Owner = owner;
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
            Owner = null;
            _lock.Release();
        }
        return true;
    }
}
