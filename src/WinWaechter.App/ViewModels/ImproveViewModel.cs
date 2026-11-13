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

/// <summary>One recommended improvement: what, one sentence why (plus its note), a checkbox – preselected unless the item says otherwise.</summary>
public sealed partial class ImprovementItemViewModel : ObservableObject
{
    readonly Action _selectionChanged;

    public ImprovementItemViewModel(Improvement item, bool canSelect, Action selectionChanged)
    {
        Item = item;
        CanSelect = canSelect;
        // spec 4.1: the recommendations are preselected; R23: an app only the Store can bring back is not
        _isSelected = item.Preselected;
        _selectionChanged = selectionChanged;
    }

    public Improvement Item { get; }
    public string Title => Item.Title;
    public string Why => Item.Note is { } note ? $"{Item.Why} {note}" : Item.Why;

    /// <summary>False without admin rights: the box is shown, but locked.</summary>
    public bool CanSelect { get; }

    [ObservableProperty]
    private bool _isSelected;

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
    readonly ISystemShell? _shell;
    readonly Action? _openOptimize;

    /// <param name="disk">The system drive; read at once (for the low-disk band) and after every check.</param>
    /// <param name="shell">Opens an allowed system link an action points to (Windows-Sicherheit).</param>
    /// <param name="openOptimize">„Platz schaffen“ once a list exists: Optimieren with a fresh scan.</param>
    public ImproveViewModel(Func<IProgress<string>?, CancellationToken, Task<ImprovementScan>> collect, OptimizerEngine engine,
        IDialogService dialogs, ActionRunner actions, bool canChange, ChangeGate gate, Func<DriveSpace?>? disk = null,
        ISystemShell? shell = null, Action? openOptimize = null)
    {
        _collect = collect;
        _engine = engine;
        _dialogs = dialogs;
        _actions = actions;
        CanChange = canChange;
        _gate = gate;
        _disk = disk;
        _shell = shell;
        _openOptimize = openOptimize;
        gate.PropertyChanged += (_, _) =>
        {
            NotifyCommands();
            OnPropertyChanged(nameof(GateHint));
        };
        // the band shows before any check
        DiskReading = ReadDiskAsync(CancellationToken.None);
    }

    /// <summary>The disk read started by the constructor (tests wait for it).</summary>
    public Task DiskReading { get; }

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

    /// <summary>True when ResultText is the summary of a run that created a session (the history has it).</summary>
    [ObservableProperty]
    private bool _hasRunResult;

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
        ShowResult(null, [], run: false);
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
            ShowResult($"Prüfung fehlgeschlagen: {ex.Message}", [], run: false);
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

    // never throws: a failed read hides the band
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
        }
        catch (Exception ex)
        {
            FileLog.Warn("Übersicht: freier Speicherplatz nicht lesbar", ex);
            LowDiskText = null;
        }
    }

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", Labels.De);

    /// <summary>
    /// „Platz schaffen“: before the first check the check fills the list („Aufräumen“ holds the safe cleanups); once a list
    /// exists, Optimieren opens with a fresh scan (safe findings preselected) and the list here stays.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task MakeSpaceAsync()
    {
        if (CheckedAt is null || _openOptimize is null) return CheckCommand.ExecuteAsync(null);
        _openOptimize();
        return Task.CompletedTask;
    }

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
        // a note (e.g. „Edge zeigt danach … verwaltet“) follows each line of its improvement, as on the Leistung page
        var lines = selected.SelectMany(i => i.Changes.Select(c => ChangeText.ConfirmLine(c) + (i.Note is { } n ? " – " + n : ""))).ToList();
        if (!_dialogs.Confirm(ConfirmTitle, message, ConfirmButton, lines)) return;

        var created = false;
        var ran = await _gate.RunAsync(this, async () => created = await RunImproveAsync(selected, ct));
        if (!ran)
        {
            ShowResult(ChangeGate.BusyHint, [], run: false);
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
        ShowResult(null, [], run: false);
        var first = selected.Where(i => i.RunFirst).ToList();
        var rest = selected.Where(i => !i.RunFirst).ToList();
        var tally = new Tally();
        var sessions = new List<ApplyResult>();
        var progress = new Progress<string>(s => ProgressText = s);

        async Task StepAsync(string title, List<Improvement> items, bool continueOnError)
        {
            if (items.Count == 0) return;
            try
            {
                var r = await _engine.ApplyAsync(title, Findings(items.SelectMany(i => i.Changes)), progress, ct, continueOnError);
                sessions.Add(r);
                tally.Add(items, r.Session.Records);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FileLog.Error($"{title} fehlgeschlagen", ex);
                tally.Skipped += items.Count;
                tally.Reasons.Add($"{title}: {ex.Message}");
            }
        }

        try
        {
            // Computerschutz first, so the restore point of the main session already works;
            // the main session runs even when this step fails
            await StepAsync(FirstStepTitle, first, continueOnError: false);
            await StepAsync(SessionTitle, rest, continueOnError: true);
        }
        catch (OperationCanceledException)
        {
            FileLog.Info("Rechner in Ordnung bringen: abgebrochen");
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }

        // per improvement, like „Jetzt verbessern (n)“; the reasons name each failed change
        var notDone = selected.Count - tally.Improved - tally.Skipped;
        var summary = $"{tally.Improved} verbessert, {tally.Skipped} übersprungen";
        if (notDone > 0) summary += $", {notDone} nicht ausgeführt";
        ShowResult(summary, tally.Reasons, run: sessions.Count > 0);
        _dialogs.Inform("Zusammenfassung", summary,
            [.. tally.Reasons, .. sessions.Select(r => r.RestorePoint.Message).Distinct()]);
        return sessions.Count > 0;
    }

    // An improvement counts as improved when all its changes were applied, as skipped when one of them failed.
    sealed class Tally
    {
        public int Improved;
        public int Skipped;
        public List<string> Reasons { get; } = [];

        // the engine journals one record per change, in the order of the changes
        public void Add(IReadOnlyList<Improvement> items, IReadOnlyList<ChangeRecord> records)
        {
            var at = 0;
            foreach (var item in items)
            {
                var mine = records.Skip(at).Take(item.Changes.Count).ToList();
                at += item.Changes.Count;
                if (mine.Any(r => r.Status == ChangeStatus.Failed)) Skipped++;
                else if (mine.Count == item.Changes.Count && mine.All(r => r.Status == ChangeStatus.Applied)) Improved++;
            }
            Reasons.AddRange(records.Where(r => r.Status == ChangeStatus.Failed).Select(r => $"{r.Description}: {r.Error}"));
        }
    }

    void ShowResult(string? text, IReadOnlyList<string> reasons, bool run)
    {
        ResultText = text;
        SkippedReasons = reasons;
        HasRunResult = run && text is not null;
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
        if (outcome.Link is { } link)
        {
            // e.g. the real-time switch blocked by tamper protection: offer Windows-Sicherheit directly
            if (!SystemLinks.IsAllowed(link)) FileLog.Warn($"Link nicht erlaubt: {link}");
            else if (_shell is not null && _dialogs.Confirm(act.Title, outcome.Message, SecurityViewModel.OpenSecurityText))
                _shell.OpenLink(link);
        }
        ShowResult(outcome.Message, [], run: false);
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
