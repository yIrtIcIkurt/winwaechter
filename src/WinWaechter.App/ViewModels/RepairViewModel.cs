using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Repair;

namespace WinWaechter.App.ViewModels;

/// <summary>One repair tool: what it is for, how long it takes, when it last ran, its button.</summary>
public sealed partial class RepairRowViewModel : ObservableObject
{
    readonly RepairViewModel _owner;
    readonly Func<SystemAction?> _action;

    internal RepairRowViewModel(RepairViewModel owner, string id, string title, string when, string duration, bool needsRestart,
        string buttonText, string? lockKey, Func<SystemAction?> action, string? emptyText = null, bool needsOwnAccount = false,
        bool cancellable = false)
    {
        _owner = owner;
        Id = id;
        Title = title;
        When = when;
        Duration = duration;
        NeedsRestart = needsRestart;
        ButtonText = buttonText;
        LockKey = lockKey;
        _action = action;
        EmptyText = emptyText;
        NeedsOwnAccount = needsOwnAccount;
        CanCancel = cancellable;
    }

    public string Id { get; }
    public string Title { get; }
    public string When { get; }
    public string Duration { get; }
    public bool NeedsRestart { get; }
    public string Facts => NeedsRestart ? $"{Duration} · Neustart nötig" : Duration;
    public string ButtonText { get; }
    internal string? LockKey { get; }
    internal string? EmptyText { get; }
    internal bool NeedsOwnAccount { get; }

    /// <summary>„Abbrechen“ is offered (system files: takes effect before sfc).</summary>
    public bool CanCancel { get; }

    internal CancellationTokenSource? Cancellation { get; set; }

    [ObservableProperty]
    private string _lastRun = "Noch nie ausgeführt";

    [ObservableProperty]
    private bool _isRecommended;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _resultText;

    /// <summary>„Beim nächsten Neustart reparieren“ after a disk scan that found problems.</summary>
    [ObservableProperty]
    private bool _showFix;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(FixCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCancel))]
    private bool _isRunning;

    public bool ShowCancel => CanCancel && IsRunning;

    /// <summary>Why the button is locked (no admin, another repair running, other account); null when free.</summary>
    public string? LockText => _owner.LockTextFor(this);

    internal void Refresh()
    {
        RunCommand.NotifyCanExecuteChanged();
        FixCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LockText));
    }

    bool CanRun() => !IsRunning && _owner.LockTextFor(this) is null;

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task RunAsync() => _owner.RunAsync(this, _action);

    [RelayCommand(CanExecute = nameof(CanRun))]
    Task FixAsync() => _owner.RunFixAsync(this);

    bool CanCancelNow() => CanCancel && IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelNow))]
    void Cancel() => Cancellation?.Cancel();
}

/// <summary>„Reparatur“: the usual Windows repairs, each with a plain result; recommended ones first.</summary>
public sealed partial class RepairViewModel : ObservableObject
{
    static readonly CultureInfo De = new("de-DE");

    readonly RepairActions _actions;
    readonly RepairChecks _checks;
    readonly ActionRunner _runner;
    readonly ActionStampStore _stamps;
    readonly char _systemDrive;
    readonly bool _ownAccount;

    public RepairViewModel(RepairActions actions, RepairChecks checks, ActionRunner runner, ActionStampStore stamps, bool canChange,
        char systemDrive, bool ownAccount = true)
    {
        _actions = actions;
        _checks = checks;
        _runner = runner;
        _stamps = stamps;
        CanChange = canChange;
        _systemDrive = systemDrive;
        _ownAccount = ownAccount;
        foreach (var key in new[] { ActivityLock.Repair, ActivityLock.Disk })
            if (runner.Lock(key) is { } l) l.PropertyChanged += (_, _) => RefreshRows();
    }

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    public ObservableCollection<RepairRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isChecking;

    IEnumerable<RepairRowViewModel> Build() =>
    [
        new(this, "repair-sfc", "Systemdateien prüfen und reparieren", "Wenn Windows abstürzt, Programme sich seltsam verhalten oder Updates scheitern.",
            "10–30 Minuten", false, "Prüfen und reparieren", ActivityLock.Repair, _actions.SystemFiles, cancellable: true),
        new(this, "repair-wu", "Windows Update reparieren", "Wenn Updates immer wieder fehlschlagen oder hängen.",
            "1–2 Minuten", false, "Reparieren", ActivityLock.Repair, _actions.WindowsUpdate),
        new(this, "repair-dns", "DNS-Cache leeren", "Wenn Webseiten nicht laden, andere aber schon.",
            "Sekunden", false, "Leeren", ActivityLock.Repair, _actions.FlushDns),
        new(this, "repair-renew", "Verbindung neu aufbauen", "Wenn das Netzwerk verbunden ist, aber nichts geht.",
            "Sekunden", false, "Neu aufbauen", ActivityLock.Repair, _actions.RenewIp),
        new(this, "repair-net", "Netzwerk komplett zurücksetzen", "Wenn nichts anderes geholfen hat.",
            "1 Minute", true, "Zurücksetzen", ActivityLock.Repair, _actions.NetworkReset),
        new(this, "repair-spool", "Druckerwarteschlange leeren", "Wenn ein Druckauftrag hängt und nichts mehr druckt.",
            "Sekunden", false, "Leeren", ActivityLock.Repair, _actions.PrintQueue, emptyText: "Die Warteschlange ist leer."),
        new(this, "repair-icons", "Symbole und Miniaturansichten neu aufbauen", "Wenn Symbole falsch oder leer angezeigt werden.",
            "Sekunden", false, "Neu aufbauen", ActivityLock.Repair, _actions.IconCache, needsOwnAccount: true),
        new(this, "repair-chkdsk-scan", "Datenträger prüfen", "Wenn Dateien beschädigt sind oder Windows Fehler meldet.",
            "Wenige Minuten", false, "Prüfen", ActivityLock.Repair, () => _actions.ScanDisk(_systemDrive)),
    ];

    // a running repair keeps its rows: no rebuild (and no DISM CheckHealth next to RestoreHealth) meanwhile
    bool CanLoad() => _runner.Lock(ActivityLock.Repair) is not { IsRunning: true };

    [RelayCommand(CanExecute = nameof(CanLoad))]
    async Task LoadAsync()
    {
        Rows.Clear();
        foreach (var r in Build())
        {
            UpdateLastRun(r);
            Rows.Add(r);
        }
        IsLoaded = true;
        IsChecking = true;
        try
        {
            // quick read-only checks decide what is recommended (seconds; no lock needed)
            var repairable = await Task.Run(() => _checks.ComponentStoreRepairableAsync(CancellationToken.None));
            var dirty = await Task.Run(() => _checks.IsDirtyAsync(_systemDrive, CancellationToken.None));
            Recommend("repair-sfc", repairable == true);
            Recommend("repair-chkdsk-scan", dirty == true);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Reparatur: Vorprüfung fehlgeschlagen", ex);
        }
        finally
        {
            IsChecking = false;
        }
    }

    void Recommend(string id, bool on)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row is null || !on) return;
        row.IsRecommended = true;
        // recommended rows go first, in the order they are recommended
        var target = Rows.TakeWhile(r => r.IsRecommended && !ReferenceEquals(r, row)).Count();
        Rows.Move(Rows.IndexOf(row), target);
    }

    void UpdateLastRun(RepairRowViewModel r)
    {
        if (_stamps.Get(r.Id) is { } s)
            r.LastRun = $"Zuletzt: {s.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm", De)} – {s.Summary}";
    }

    internal string? LockTextFor(RepairRowViewModel r)
    {
        if (!CanChange) return AppEnvironment.AdminNotice;
        if (r.NeedsOwnAccount && !_ownAccount) return "Nur für das angemeldete Benutzerkonto möglich.";
        return _runner.Lock(r.LockKey) is { IsRunning: true } l ? l.BusyText : null;
    }

    void RefreshRows()
    {
        LoadCommand.NotifyCanExecuteChanged();
        foreach (var r in Rows) r.Refresh();
    }

    internal async Task RunAsync(RepairRowViewModel row, Func<SystemAction?> make)
    {
        // building an action may read the system (network settings): off the UI thread
        var action = await Task.Run(make);
        if (action is null)
        {
            row.ResultText = row.EmptyText;
            return;
        }
        await RunActionAsync(row, action);
        if (row.Id == "repair-chkdsk-scan") row.ShowFix = _actions.ScanFoundErrors;
    }

    internal async Task RunFixAsync(RepairRowViewModel row)
    {
        // planned: the button goes away; declined or failed: it stays
        if (await RunActionAsync(row, _actions.FixDiskOnRestart(_systemDrive)) is { Success: true }) row.ShowFix = false;
    }

    async Task<ActionOutcome?> RunActionAsync(RepairRowViewModel row, SystemAction action)
    {
        // busy only once the action really runs (after the confirmation)
        var run = action with
        {
            RunAsync = (p, ct) =>
            {
                row.IsRunning = true;
                return action.RunAsync(p, ct);
            },
        };
        row.Cancellation = new CancellationTokenSource();
        try
        {
            var outcome = await _runner.RunAsync(this, run, new Progress<string>(t => row.ProgressText = t), row.Cancellation.Token);
            if (outcome is null) return null;
            row.ResultText = outcome.Message;
            UpdateLastRun(row);
            return outcome;
        }
        finally
        {
            row.ProgressText = null;
            row.IsRunning = false;
            row.Cancellation.Dispose();
            row.Cancellation = null;
            RefreshRows();
        }
    }
}
