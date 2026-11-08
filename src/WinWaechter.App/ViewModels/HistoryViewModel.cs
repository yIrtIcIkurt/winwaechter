using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;

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
    public bool CanUndo => Record.Status == ChangeStatus.Applied && !IsPurged;
    public string? Hint => IsPurged && Record.Status == ChangeStatus.Applied ? "Ablage geleert" : null;
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

    public HistoryViewModel(SessionStore store, OptimizerEngine engine, AblageJanitor janitor, IDialogService dialogs,
        bool canChange, ChangeGate gate)
    {
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PurgeAblageCommand))]
    private long _ablageBytes;

    [ObservableProperty]
    private string _ablageText = "–";

    public bool IsEmpty => Sessions.Count == 0;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedDir = SelectedSession?.Journal.Context.SessionDir;
        IReadOnlyList<SessionJournal> journals;
        long bytes;
        try
        {
            (journals, bytes) = await Task.Run(() =>
            {
                var list = _store.List();
                return (list, list.Sum(j => DirSize(j.Context.AblageDir)));
            });
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
        AblageBytes = bytes;
        AblageText = Labels.Size(bytes);
        OnPropertyChanged(nameof(IsEmpty));
    }

    static long DirSize(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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
        await RunUndoAsync(() => _engine.UndoSession(s.Journal));
    }

    bool CanUndoOne(RecordViewModel? r) => CanChange && !IsBusy && !_gate.IsBusy && r is { CanUndo: true };

    [RelayCommand(CanExecute = nameof(CanUndoOne))]
    private Task UndoOneAsync(RecordViewModel? r) =>
        r is null ? Task.CompletedTask : RunUndoAsync(() => _engine.UndoOne(r.Session.Journal, r.Record.Id));

    async Task RunUndoAsync(Func<UndoResult> undo)
    {
        if (!await _gate.RunAsync(this, () => UndoCoreAsync(undo))) _dialogs.Inform("Rückgängig", ChangeGate.BusyHint);
    }

    async Task UndoCoreAsync(Func<UndoResult> undo)
    {
        IsBusy = true;
        try
        {
            var res = await Task.Run(undo);
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
            IsBusy = false;
        }
        await RefreshAsync();
    }

    bool CanPurge() => !IsBusy && !_gate.IsBusy && AblageBytes > 0;

    [RelayCommand(CanExecute = nameof(CanPurge))]
    private async Task PurgeAblageAsync()
    {
        if (!_dialogs.Confirm("Ablage jetzt leeren",
                $"Alle Dateien in der Ablage ({AblageText}) werden endgültig gelöscht. Danach lassen sich diese Dateien nicht mehr wiederherstellen, auch nicht über den Verlauf.",
                "Endgültig leeren", final: true))
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
