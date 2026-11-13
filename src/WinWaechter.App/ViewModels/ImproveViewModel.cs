using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>One recommended improvement: what, one sentence why, a preselected checkbox.</summary>
public sealed partial class ImprovementItemViewModel : ObservableObject
{
    readonly Action _selectionChanged;

    public ImprovementItemViewModel(Improvement item, bool canSelect, Action selectionChanged)
    {
        Item = item;
        CanSelect = canSelect;
        _selectionChanged = selectionChanged;
    }

    public Improvement Item { get; }
    public string Title => Item.Title;
    public string Why => Item.Why;

    /// <summary>False without admin rights: the box is shown, but locked.</summary>
    public bool CanSelect { get; }

    // spec 4.1: every recommendation in the list is preselected
    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}

/// <summary>One area heading („Aufräumen“, „Sicherheit“ …) with its improvements.</summary>
public sealed record ImprovementGroupViewModel(string Title, IReadOnlyList<ImprovementItemViewModel> Items);

/// <summary>„Außerdem empfohlen“: a one-off action with its own button, never part of „Jetzt verbessern“.</summary>
public sealed class ActionSuggestionViewModel
{
    public ActionSuggestionViewModel(ActionSuggestion suggestion, Func<ActionSuggestionViewModel, Task> run, Func<bool> canRun)
    {
        Suggestion = suggestion;
        RunCommand = new AsyncRelayCommand(() => run(this), canRun);
    }

    public ActionSuggestion Suggestion { get; }
    public string Title => Suggestion.Title;
    public string Why => Suggestion.Why;
    public string ButtonText => Suggestion.Action.ButtonText;
    public IAsyncRelayCommand RunCommand { get; }
}

/// <summary>
/// „Rechner in Ordnung bringen“ (spec 4.1): checks every area, shows one preselected list and applies it in one confirmed
/// run – the Computerschutz first as its own session, the rest in one session that goes on after a failed change.
/// </summary>
public sealed partial class ImproveViewModel : ObservableObject
{
    public const string ConfirmTitle = "Rechner in Ordnung bringen";
    public const string SessionTitle = "Rechner in Ordnung bringen";
    public const string FirstStepTitle = "Computerschutz einschalten";
    public const string ConfirmButton = "Jetzt verbessern";
    public const string CheckingText = "Alle Bereiche werden geprüft …";

    readonly Func<IProgress<string>?, CancellationToken, Task<ImprovementScan>> _collect;
    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly ActionRunner _actions;
    readonly ChangeGate _gate;
    readonly Func<DriveSpace?>? _disk;

    public ImproveViewModel(Func<IProgress<string>?, CancellationToken, Task<ImprovementScan>> collect, OptimizerEngine engine,
        IDialogService dialogs, ActionRunner actions, bool canChange, ChangeGate gate, Func<DriveSpace?>? disk = null)
    {
        _collect = collect;
        _engine = engine;
        _dialogs = dialogs;
        _actions = actions;
        CanChange = canChange;
        _gate = gate;
        _disk = disk;
        gate.PropertyChanged += (_, _) =>
        {
            NotifyCommands();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Raised after a run created sessions (the history should show the newest one).</summary>
    public event EventHandler? ChangesApplied;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    public ObservableCollection<ImprovementGroupViewModel> Groups { get; } = [];
    public ObservableCollection<ActionSuggestionViewModel> Suggestions { get; } = [];

    IEnumerable<ImprovementItemViewModel> AllItems => Groups.SelectMany(g => g.Items);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(MakeSpaceCommand))]
    private bool _isBusy;

    /// <summary>True while the check runs (not while changes are applied).</summary>
    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>When the last check finished; null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllGood))]
    [NotifyPropertyChangedFor(nameof(AllGoodText))]
    [NotifyPropertyChangedFor(nameof(OpenCount))]
    private DateTimeOffset? _checkedAt;

    /// <summary>Areas the last check could not read („Leistung: nicht prüfbar“); null when all were read.</summary>
    [ObservableProperty]
    private string? _uncheckedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImproveText))]
    [NotifyCanExecuteChangedFor(nameof(ImproveCommand))]
    private int _selectedCount;

    /// <summary>„n verbessert, m übersprungen“ after a run, or the message of the last action.</summary>
    [ObservableProperty]
    private string? _resultText;

    /// <summary>One line per failed change of the last run: description and error.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _skippedReasons = [];

    [ObservableProperty]
    private string? _lowDiskText;

    public string ImproveText => $"Jetzt verbessern ({SelectedCount})";

    public bool HasItems => Groups.Count > 0;
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Nothing to improve and nothing else recommended after a check.</summary>
    public bool IsAllGood => CheckedAt is not null && !HasItems && !HasSuggestions;

    public string? AllGoodText => CheckedAt is { } at
        ? $"Ihr Rechner ist in Ordnung (geprüft am {at.ToString("dd.MM.yyyy HH:mm", Labels.De)})."
        : null;

    /// <summary>Improvements plus suggestions of the last check; null before the first check.</summary>
    public int? OpenCount => CheckedAt is null ? null : AllItems.Count() + Suggestions.Count;

    /// <summary>What frees the most space when the system drive is nearly full.</summary>
    public static IReadOnlyList<string> LowDiskLines { get; } =
        ["Zwischenspeicher und temporäre Dateien (in der Liste unter „Aufräumen“)", "Ablage im Verlauf leeren"];

    bool CanCheck() => !IsBusy;

    /// <summary>Runs every area check (nothing changes) and shows the recommendations.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync(CancellationToken ct)
    {
        if (IsBusy) return Task.CompletedTask;
        // a new check by hand: the result of an earlier run no longer applies
        ResultText = null;
        SkippedReasons = [];
        return LoadAsync(ct);
    }

    // The check itself; after a run or an action it shows the new state and keeps that result.
    async Task LoadAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsChecking = true;
        ProgressText = CheckingText;
        try
        {
            var scan = await _collect(new Progress<string>(s => ProgressText = s), ct);
            Show(scan);
            await ReadDiskAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Error("Rechner in Ordnung bringen: Prüfung fehlgeschlagen", ex);
            ResultText = $"Prüfung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsChecking = false;
            IsBusy = false;
        }
    }

    void Show(ImprovementScan scan)
    {
        Groups.Clear();
        foreach (var area in scan.Items.GroupBy(i => i.Area).OrderBy(g => g.Key))
            Groups.Add(new ImprovementGroupViewModel(ImprovementAreas.Title(area.Key),
                area.Select(i => new ImprovementItemViewModel(i, CanChange, UpdateSelection)).ToList()));
        Suggestions.Clear();
        foreach (var s in scan.Actions) Suggestions.Add(new ActionSuggestionViewModel(s, RunSuggestionAsync, CanRunSuggestion));
        UncheckedText = scan.Unchecked.Count > 0 ? "Nicht geprüft: " + string.Join(" · ", scan.Unchecked) : null;
        UpdateSelection();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasSuggestions));
        CheckedAt = scan.At;
        // a check with the same time stamp raises nothing on its own
        OnPropertyChanged(nameof(IsAllGood));
        OnPropertyChanged(nameof(OpenCount));
    }

    async Task ReadDiskAsync(CancellationToken ct)
    {
        if (_disk is null) return;
        try
        {
            var d = await Task.Run(_disk, ct);
            LowDiskText = d is not null && DiskSpace.IsLow(d)
                ? $"Auf Laufwerk {d.Drive.TrimEnd('\\', '/')} sind nur noch {Gb(d.FreeBytes)} GB frei."
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Übersicht: freier Speicherplatz nicht lesbar", ex);
            LowDiskText = null;
        }
    }

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", Labels.De);

    /// <summary>„Platz schaffen“: the check fills the list, where „Aufräumen“ holds the safe cleanups.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task MakeSpaceAsync() => CheckCommand.ExecuteAsync(null);

    bool CanImprove() => CanChange && !IsBusy && !_gate.IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanImprove))]
    private async Task ImproveAsync(CancellationToken ct)
    {
        if (!CanImprove()) return;
        var selected = AllItems.Where(i => i.IsSelected).Select(i => i.Item).ToList();
        var changes = selected.SelectMany(i => i.Changes).ToList();
        if (changes.Count == 0) return;
        var message = selected.Count == 1
            ? "1 Verbesserung wird angewendet. Sie steht danach im Verlauf und lässt sich dort als Ganzes oder einzeln rückgängig machen."
            : $"{selected.Count} Verbesserungen werden angewendet. Sie stehen danach im Verlauf und lassen sich dort als Ganzes oder einzeln rückgängig machen.";
        if (selected.Any(i => i.RunFirst)) message += " " + SecurityViewModel.SysprotectNote;
        if (!_dialogs.Confirm(ConfirmTitle, message, ConfirmButton, changes.Select(ChangeText.ConfirmLine).ToList())) return;

        var created = false;
        var ran = await _gate.RunAsync(this, async () => created = await RunImproveAsync(selected, ct));
        if (!ran)
        {
            ResultText = ChangeGate.BusyHint;
            SkippedReasons = [];
            return;
        }
        if (created) ChangesApplied?.Invoke(this, EventArgs.Empty);
        // the overview shows the new state
        await LoadAsync(CancellationToken.None);
    }

    /// <summary>True when at least one session was created.</summary>
    async Task<bool> RunImproveAsync(IReadOnlyList<Improvement> selected, CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        ResultText = null;
        SkippedReasons = [];
        var first = selected.Where(i => i.RunFirst).SelectMany(i => i.Changes).ToList();
        var rest = selected.Where(i => !i.RunFirst).SelectMany(i => i.Changes).ToList();
        var results = new List<ApplyResult>();
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            // Computerschutz first, so the restore point of the main session already works
            if (first.Count > 0) results.Add(await _engine.ApplyAsync(FirstStepTitle, Findings(first), progress, ct));
            if (rest.Count > 0)
                results.Add(await _engine.ApplyAsync(SessionTitle, Findings(rest), progress, ct, continueOnError: true));
        }
        catch (OperationCanceledException)
        {
            FileLog.Info("Rechner in Ordnung bringen: abgebrochen");
        }
        catch (Exception ex)
        {
            FileLog.Error("Rechner in Ordnung bringen fehlgeschlagen", ex);
            _dialogs.Inform("Verbessern fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }

        var applied = results.Sum(r => r.Applied);
        var failed = results.Sum(r => r.Failed);
        var notDone = first.Count + rest.Count - applied - failed;
        var reasons = results.SelectMany(r => r.Session.Records)
            .Where(r => r.Status == ChangeStatus.Failed)
            .Select(r => $"{r.Description}: {r.Error}")
            .ToList();
        var summary = $"{applied} verbessert, {failed} übersprungen";
        if (notDone > 0) summary += $", {notDone} nicht ausgeführt";
        ResultText = summary;
        SkippedReasons = reasons;
        if (results.Count > 0)
            _dialogs.Inform("Zusammenfassung", summary, [.. reasons, .. results.Select(r => r.RestorePoint.Message).Distinct()]);
        return results.Count > 0;
    }

    static List<Finding> Findings(IEnumerable<IChange> changes) =>
        changes.Select(c => new Finding($"improve|{c.Kind}|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [],
            Rating.Info, true, c)).ToList();

    bool CanRunSuggestion() => CanChange && !IsBusy && !_gate.IsBusy;

    // The runner confirms (and says „endgültig“), takes the gate itself and logs – so this runs outside any gate of ours.
    async Task RunSuggestionAsync(ActionSuggestionViewModel s)
    {
        if (!CanRunSuggestion()) return;
        var act = s.Suggestion.Action;
        // busy only once the action really runs, i.e. after the confirmation and inside the gate
        var run = act with
        {
            RunAsync = (p, ct) =>
            {
                IsBusy = true;
                ProgressText = $"{act.Title} …";
                return act.RunAsync(p, ct);
            },
        };
        ActionOutcome? outcome;
        try
        {
            outcome = await _actions.RunAsync(this, run, new Progress<string>(t => ProgressText = t));
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
        if (outcome is null) return;
        ResultText = outcome.Message;
        SkippedReasons = [];
        await LoadAsync(CancellationToken.None);
    }

    void UpdateSelection() => SelectedCount = AllItems.Count(i => i.IsSelected);

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    void NotifyCommands()
    {
        ImproveCommand.NotifyCanExecuteChanged();
        foreach (var s in Suggestions) s.RunCommand.NotifyCanExecuteChanged();
    }
}
