using CommunityToolkit.Mvvm.ComponentModel;
using WinWaechter.Core.Actions;

namespace WinWaechter.App.ViewModels;

/// <summary>
/// A line of a list that a batch run works through one by one: it shows its own state
/// (Wartet, Läuft …, ✓ Fertig, ✗ Fehlgeschlagen with the reason, Ausgelassen) instead of only a counter above the list.
/// </summary>
public abstract partial class ItemRowBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDone), nameof(ShowChip), nameof(StateLabel), nameof(StateTone), nameof(ShowStateText),
        nameof(ShowStateView))]
    private ItemState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStateText))]
    private string? _stateText;

    /// <summary>The state column shows (instead of a line's own button) while the line takes part in a run or since.</summary>
    public bool ShowStateView => State != ItemState.None;
    public bool IsRunning => State == ItemState.Running;
    public bool IsDone => State == ItemState.Done;
    public bool ShowChip => State is ItemState.Waiting or ItemState.Done or ItemState.Failed or ItemState.Skipped;

    public string? StateLabel => State switch
    {
        ItemState.Waiting => "Wartet",
        ItemState.Running => "Läuft …",
        ItemState.Done => "✓ Fertig",
        ItemState.Failed => "✗ Fehlgeschlagen",
        ItemState.Skipped => "Ausgelassen",
        _ => null,
    };

    public Tone StateTone => State switch
    {
        ItemState.Done => Tone.Good,
        ItemState.Failed => Tone.Alarm,
        ItemState.Skipped => Tone.Watch,
        _ => Tone.Neutral,
    };

    public bool ShowStateText => State is ItemState.Failed or ItemState.Skipped && !string.IsNullOrWhiteSpace(StateText);

    public void SetState(ItemState state, string? text = null)
    {
        StateText = text;
        State = state;
    }
}

public static class ItemRows
{
    /// <summary>Before the run: every line that will run waits.</summary>
    public static void Begin(IEnumerable<ItemRowBase> rows)
    {
        foreach (var r in rows) r.SetState(ItemState.Waiting);
    }

    /// <summary>After the run: a line that never got its turn (cancelled, stopped after a failure) says so.</summary>
    public static void Finish(IEnumerable<ItemRowBase> rows)
    {
        foreach (var r in rows)
            if (r.State is ItemState.Waiting or ItemState.Running) r.SetState(ItemState.Skipped, "Nicht ausgeführt.");
    }
}

/// <summary>
/// Hands reports to the UI thread when there is one (in the app), otherwise runs them at once (tests, background).
/// In the app the reports are posted in order before the run's own continuation, so the last state always arrives first.
/// </summary>
public sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Report(T value)
    {
        if (_context is null) handler(value);
        else _context.Post(_ => handler(value), null);
    }
}
