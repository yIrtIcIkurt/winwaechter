using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>One journalled change inside a session.</summary>
public sealed class RecordViewModel(ChangeRecord record, SessionViewModel session)
{
    public ChangeRecord Record { get; } = record;
    public SessionViewModel Session { get; } = session;
    public string Description => Record.Description;
    public string StatusText => Labels.Status(Record.Status);
    public Tone StatusTone => Labels.StatusTone(Record.Status);
    public string TimeText => Labels.Date(Record.At);
    public string? Error => Record.Error;
    public bool IsPurged => Record.After.TryGetValue("purged", out var p) && p == "true";
    public bool MovedNothing => OptimizerEngine.MovedNothing(Record);
    public bool CanUndo => OptimizerEngine.IsUndoable(Record) && !IsPurged;
    /// <summary>Store page of an app that only the Store can bring back (appx without image copy); otherwise null.</summary>
    public string? StoreLink =>
        Record.Kind == "appx" && Record.After.GetValueOrDefault("restorable") == "false"
        && WinAppPackages.IsValidFamily(Record.Before.GetValueOrDefault("family"))
            ? SystemLinks.StoreLink(Record.Before["family"]!)
            : null;
    public bool HasStoreLink => StoreLink is not null;
    public string? Hint =>
        MovedNothing ? "Nichts verschoben"
        : IsPurged && Record.Status == ChangeStatus.Applied ? "Ablage geleert"
        : HasStoreLink ? UndoLimits.StoreOnly
        : Record.Kind == "sysprotect" && Record.Status == ChangeStatus.Applied ? PartialText
        : null;
    public const string PartialText = "Nur teilweise rückgängig: Der Computerschutz bleibt an.";
    /// <summary>A greyed-out „Rückgängig“ with the reason under it (nothing can be undone here, and no Store button).</summary>
    public bool ShowDisabledUndo => Hint is not null && !CanUndo && !HasStoreLink;
}

/// <summary>One optimizer session with its records.</summary>
public sealed class SessionViewModel
{
    public SessionViewModel(SessionJournal journal)
    {
        Journal = journal;
        Records = new(journal.Records.Select(r => new RecordViewModel(r, this)));
    }

    public SessionJournal Journal { get; }
    public ObservableCollection<RecordViewModel> Records { get; }
    public string Title => Journal.Title;
    public string CreatedText => Labels.Date(Journal.Created);
    public bool CanUndoAll => Records.Any(r => r.CanUndo);

    public string Summary
    {
        get
        {
            var total = Records.Count;
            var open = Records.Count(r => r.CanUndo);
            var changes = total == 1 ? "1 Änderung" : $"{total} Änderungen";
            return open == total || open == 0 ? changes : $"{changes}, {open} aktiv";
        }
    }
}

/// <summary>"Verlauf": sessions, undo and the Ablage.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    readonly SessionStore _store;
    readonly OptimizerEngine _engine;
    readonly AblageJanitor _janitor;
    readonly IDialogService _dialogs;
    readonly ChangeGate _gate;
    readonly ISystemShell? _shell;

    public HistoryViewModel(SessionStore store, OptimizerEngine engine, AblageJanitor janitor, IDialogService dialogs,
        bool canChange, ChangeGate gate, ISystemShell? shell = null)
    {
        _shell = shell;
        _store = store;
        _engine = engine;
        _janitor = janitor;
        _dialogs = dialogs;
        CanChange = canChange;
        _gate = gate;
        gate.PropertyChanged += (_, _) =>
        {
            UndoAllCommand.NotifyCanExecuteChanged();
            UndoOneCommand.NotifyCanExecuteChanged();
            PurgeAblageCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    /// <summary>Page-level message when the history could not be read.</summary>
    [ObservableProperty]
    private string? _errorText;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAllCommand))]
    private SessionViewModel? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAllCommand), nameof(UndoOneCommand), nameof(PurgeAblageCommand))]
    private bool _isBusy;

    /// <summary>Current undo step while undoing ("(2/5) …"), empty otherwise.</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>Undo progress 0..1 = (Index − 1) / Count.</summary>
    [ObservableProperty]
    private double _progressValue;

    // Progress callbacks are posted; one from a finished run must not overwrite the cleared text.
    readonly object _progressLock = new();
    object? _progressRun;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PurgeAblageCommand))]
    private long _ablageBytes;

    [ObservableProperty]
    private string _ablageText = "–";

    /// <summary>Folders of unfinished moves that a purge keeps (null when there are none).</summary>
    [ObservableProperty]
    private string? _keptText;

    internal static string? KeptFoldersText(int n) =>
        n <= 0 ? null : $"{n} Ordner aus unvollständigen Vorgängen bleiben erhalten.";

    /// <summary>Whole program folders (Kind "folder") a purge would delete; named in the purge dialog.</summary>
    int _programFolders;

    internal static string? ProgramFoldersText(int n) =>
        n <= 0 ? null
        : n == 1 ? "Darin enthalten: 1 Programmordner aus „Verwaiste Ordner“."
        : $"Darin enthalten: {n} Programmordner aus „Verwaiste Ordner“.";

    public bool IsEmpty => Sessions.Count == 0;

    bool _selectNewestNext;

    /// <summary>Refresh after a new change was applied and select that new (newest) session,
    /// so „Alles rückgängig“ never acts on an older session the user looked at before.</summary>
    public Task ShowNewestAsync()
    {
        _selectNewestNext = true;
        return RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedDir = _selectNewestNext ? null : SelectedSession?.Journal.Context.SessionDir;
        _selectNewestNext = false;
        IReadOnlyList<SessionJournal> journals;
        AblagePreview preview;
        try
        {
            // size = exactly what "Ablage jetzt leeren" would delete (kept folders and links excluded)
            (journals, preview) = await Task.Run(() => (_store.List(), _janitor.PreviewNow()));
            ErrorText = null;
        }
        catch (Exception ex)
        {
            FileLog.Error("Verlauf nicht lesbar", ex);
            ErrorText = $"Der Verlauf konnte nicht gelesen werden: {ex.Message}";
            return;
        }
        Sessions.Clear();
        foreach (var j in journals) Sessions.Add(new SessionViewModel(j));
        SelectedSession = Sessions.FirstOrDefault(s => s.Journal.Context.SessionDir == selectedDir) ?? Sessions.FirstOrDefault();
        AblageBytes = preview.PurgeableBytes;
        AblageText = Labels.Size(preview.PurgeableBytes);
        KeptText = KeptFoldersText(preview.KeptFolders);
        _programFolders = preview.ProgramFolders;
        OnPropertyChanged(nameof(IsEmpty));
    }

    bool CanUndoAll(SessionViewModel? s) => CanChange && !IsBusy && !_gate.IsBusy && s is { CanUndoAll: true };

    [RelayCommand(CanExecute = nameof(CanUndoAll))]
    private async Task UndoAllAsync(SessionViewModel? s)
    {
        if (s is null) return;
        if (!_dialogs.Confirm("Alles rückgängig",
                "Alle noch aktiven Änderungen dieser Sitzung werden in umgekehrter Reihenfolge zurückgenommen. Was sich inzwischen von außen geändert hat, wird übersprungen und gemeldet.",
                "Rückgängig machen"))
            return;
        await RunUndoAsync(p => _engine.UndoSession(s.Journal, p));
    }

    /// <summary>Opens the Store page of an app that was removed without a local copy (needs no admin rights).</summary>
    [RelayCommand]
    private void OpenStore(RecordViewModel? r)
    {
        if (r?.StoreLink is { } link) _shell?.OpenLink(link);
    }

    bool CanUndoOne(RecordViewModel? r) => CanChange && !IsBusy && !_gate.IsBusy && r is { CanUndo: true };

    [RelayCommand(CanExecute = nameof(CanUndoOne))]
    private Task UndoOneAsync(RecordViewModel? r) =>
        r is null ? Task.CompletedTask : RunUndoAsync(p => _engine.UndoOne(r.Session.Journal, r.Record.Id, p));

    async Task RunUndoAsync(Func<IProgress<UndoProgress>, UndoResult> undo)
    {
        if (!await _gate.RunAsync(this, () => UndoCoreAsync(undo))) _dialogs.Inform("Rückgängig", ChangeGate.BusyHint);
    }

    async Task UndoCoreAsync(Func<IProgress<UndoProgress>, UndoResult> undo)
    {
        IsBusy = true;
        var run = new object();
        lock (_progressLock) _progressRun = run;
        var progress = new Progress<UndoProgress>(u =>
        {
            lock (_progressLock)
            {
                if (_progressRun != run) return;
                ProgressText = u.Text;
                ProgressValue = u.Count > 0 ? (double)(u.Index - 1) / u.Count : 0;
            }
        });
        try
        {
            var res = await Task.Run(() => undo(progress));
            var lines = new List<string> { $"Rückgängig gemacht: {res.Undone}" };
            if (res.SkippedDrift > 0) lines.Add($"Übersprungen: {res.SkippedDrift}");
            if (res.Failed > 0) lines.Add($"Fehlgeschlagen: {res.Failed}");
            lines.AddRange(res.Messages);
            _dialogs.Inform("Rückgängig",
                res.Failed == 0 && res.SkippedDrift == 0 ? "Fertig." : "Nicht alles ließ sich zurücknehmen. Die Gründe:",
                lines);
        }
        catch (Exception ex)
        {
            FileLog.Error("Rückgängig fehlgeschlagen", ex);
            _dialogs.Inform("Rückgängig fehlgeschlagen", ex.Message);
        }
        finally
        {
            lock (_progressLock)
            {
                _progressRun = null;
                ProgressText = "";
                ProgressValue = 0;
            }
            IsBusy = false;
        }
        await RefreshAsync();
    }

    bool CanPurge() => CanChange && !IsBusy && !_gate.IsBusy && AblageBytes > 0;

    [RelayCommand(CanExecute = nameof(CanPurge))]
    private async Task PurgeAblageAsync()
    {
        var message = $"Alle Dateien in der Ablage ({AblageText}) werden endgültig gelöscht. Danach lassen sich diese Dateien nicht mehr wiederherstellen, auch nicht über den Verlauf.";
        if (ProgramFoldersText(_programFolders) is { } programs) message += " " + programs;
        if (KeptText is not null) message += " " + KeptText;
        if (!_dialogs.Confirm("Ablage jetzt leeren", message, "Endgültig leeren", final: true))
            return;
        if (!await _gate.RunAsync(this, PurgeCoreAsync)) _dialogs.Inform("Ablage jetzt leeren", ChangeGate.BusyHint);
    }

    async Task PurgeCoreAsync()
    {
        IsBusy = true;
        try
        {
            var freed = await Task.Run(_janitor.PurgeNow);
            _dialogs.Inform("Ablage geleert", $"{Labels.Size(freed)} endgültig gelöscht.");
        }
        catch (Exception ex)
        {
            FileLog.Error("Ablage leeren fehlgeschlagen", ex);
            _dialogs.Inform("Ablage leeren fehlgeschlagen", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }
}
