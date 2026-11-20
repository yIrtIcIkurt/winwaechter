using CommunityToolkit.Mvvm.ComponentModel;
using WinWaechter.Core.Actions;

namespace WinWaechter.App.ViewModels;

/// <summary>
/// A line of a list that a batch run works through one by one: it shows its own state
/// (Wartet, Läuft …, ✓ Fertig, ✗ Fehlgeschlagen with the reason, Ausgelassen) instead of only a counter above the list.
/// The names start with „Run“ so they never meet a line's own state (e.g. a privacy setting's „An“/„Aus“).
/// </summary>
public abstract partial class ItemRowBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsDone), nameof(ShowRunChip), nameof(RunLabel), nameof(RunTone), nameof(ShowRunText),
        nameof(ShowRunState))]
    private ItemState _runState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRunText))]
    private string? _runText;

    /// <summary>The state column shows (instead of a line's own button or chip) while the line takes part in a run or since.</summary>
    public bool ShowRunState => RunState != ItemState.None;
    public bool IsRunning => RunState == ItemState.Running;
    public bool IsDone => RunState == ItemState.Done;
    public bool ShowRunChip => RunState is ItemState.Waiting or ItemState.Done or ItemState.Failed or ItemState.Skipped;

    public string? RunLabel => RunState switch
    {
        ItemState.Waiting => "Wartet",
        ItemState.Running => "Läuft …",
        ItemState.Done => "✓ Fertig",
        ItemState.Failed => "✗ Fehlgeschlagen",
        ItemState.Skipped => "Ausgelassen",
        _ => null,
    };

    public Tone RunTone => RunState switch
    {
        ItemState.Done => Tone.Good,
        ItemState.Failed => Tone.Alarm,
        ItemState.Skipped => Tone.Watch,
        _ => Tone.Neutral,
    };

    public bool ShowRunText => RunState is ItemState.Failed or ItemState.Skipped && !string.IsNullOrWhiteSpace(RunText);

    public void SetState(ItemState state, string? text = null)
    {
        RunText = text;
        RunState = state;
    }
}

public static class ItemRows
{
    /// <summary>Before the run: every line that will run waits.</summary>
    public static void Begin(IEnumerable<ItemRowBase> rows)
    {
        foreach (var r in rows) r.SetState(ItemState.Waiting);
    }

    /// <summary>
    /// Before a run: the lines wait, and the returned progress moves each line (matched by reference to the item the run
    /// reports, e.g. its Finding) to Running, Done or Failed. A line can stand for several items (an improvement with
    /// several changes): it runs until all of them are through, then shows the worst result (Failed over Skipped over Done).
    /// </summary>
    public static IProgress<ItemProgress> Start(IEnumerable<(object Item, ItemRowBase Row)> lines)
    {
        var map = new Dictionary<object, List<ItemRowBase>>(ReferenceEqualityComparer.Instance);
        var tally = new Dictionary<ItemRowBase, Tally>(ReferenceEqualityComparer.Instance);
        foreach (var (item, row) in lines)
        {
            if (!map.TryGetValue(item, out var list)) map[item] = list = [];
            list.Add(row);
            if (!tally.TryGetValue(row, out var t)) tally[row] = t = new Tally();
            t.Total++;
            row.SetState(ItemState.Waiting);
        }
        return new UiProgress<ItemProgress>(i =>
        {
            if (!map.TryGetValue(i.Item, out var hit)) return;
            foreach (var r in hit)
            {
                var t = tally[r];
                if (i.State is ItemState.Running or ItemState.Waiting or ItemState.None)
                {
                    r.SetState(ItemState.Running);
                    continue;
                }
                t.Finished++;
                if (Rank(i.State) > Rank(t.Worst) || (i.State == t.Worst && t.Text is null)) { t.Worst = i.State; t.Text = i.Text; }
                if (t.Finished >= t.Total) r.SetState(t.Worst, t.Text);
                else r.SetState(ItemState.Running);
            }
        });
    }

    static int Rank(ItemState s) => s switch { ItemState.Failed => 3, ItemState.Skipped => 2, ItemState.Done => 1, _ => 0 };

    sealed class Tally
    {
        public int Total;
        public int Finished;
        public ItemState Worst = ItemState.Done;
        public string? Text;
    }

    /// <summary>After the run: a line that never got its turn (cancelled, stopped after a failure) says so.</summary>
    public static void Finish(IEnumerable<ItemRowBase> rows)
    {
        foreach (var r in rows)
            if (r.RunState is ItemState.Waiting or ItemState.Running) r.SetState(ItemState.Skipped, "Nicht ausgeführt.");
    }
}

/// <summary>
/// Hands reports to the UI thread when there is one (in the app), otherwise runs them at once (tests, background).
/// In the app the reports are posted in order before the run's own continuation, so the last state always arrives first.
/// </summary>
public sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    // only the WPF dispatcher counts; a test runner's own context would make the order of reports a race
    readonly SynchronizationContext? _context =
        SynchronizationContext.Current as System.Windows.Threading.DispatcherSynchronizationContext;

    public void Report(T value)
    {
        if (_context is null) handler(value);
        else _context.Post(_ => handler(value), null);
    }
}
