using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>"Schutz": Defender status, scans, clean-up, PUA protection and the own heuristics.</summary>
public sealed partial class ProtectViewModel : ObservableObject
{
    public delegate Task<IReadOnlyList<Finding>> HeuristicScan(IProgress<string>? progress, CancellationToken ct);

    readonly IDefender _defender;
    readonly HeuristicScan _heuristics;
    readonly OptimizerEngine _engine;
    readonly Func<string, int> _killByPath;
    readonly IDialogService _dialogs;
    readonly ChangeGate _gate;
    readonly ActionRunner? _actions;
    readonly SystemAction? _enableRealTime;
    readonly ISystemShell? _shell;
    CancellationTokenSource? _defenderCts;

    /// <param name="enableRealTime">The same action as on the Sicherheit page (SecurityActionSet.EnableRealTime).</param>
    /// <param name="shell">Opens Windows-Sicherheit when Windows blocked the switch.</param>
    public ProtectViewModel(IDefender defender, HeuristicScan heuristics, OptimizerEngine engine,
        Func<string, int> killByPath, IDialogService dialogs, bool canChange, ChangeGate gate,
        ActionRunner? actions = null, SystemAction? enableRealTime = null, ISystemShell? shell = null)
    {
        _actions = actions;
        _enableRealTime = enableRealTime;
        _shell = shell;
        _defender = defender;
        _heuristics = heuristics;
        _engine = engine;
        _killByPath = killByPath;
        _dialogs = dialogs;
        CanChange = canChange;
        _gate = gate;
        gate.PropertyChanged += (_, _) =>
        {
            CleanCommand.NotifyCanExecuteChanged();
            EnablePuaCommand.NotifyCanExecuteChanged();
            DisableCommand.NotifyCanExecuteChanged();
            KillProcessCommand.NotifyCanExecuteChanged();
            EnableRealTimeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    /// <summary>Raised after a journalled change (the history should reload).</summary>
    public event EventHandler? ChangesApplied;

    /// <summary>Raised when status, threats or heuristic findings changed.</summary>
    public event EventHandler? StateChanged;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;
    public ElapsedClock Clock { get; } = new();

    // ---- Defender status ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsAvailable), nameof(RealTimeText), nameof(RealTimeTone),
        nameof(SignatureAgeText), nameof(SignatureTone), nameof(LastScanText), nameof(TamperText),
        nameof(PuaText), nameof(ShowPuaSuggestion), nameof(DefenderNote), nameof(ThreatSummary), nameof(ShowRealTimeFix))]
    [NotifyCanExecuteChangedFor(nameof(EnablePuaCommand), nameof(EnableRealTimeCommand))]
    private DefenderStatus? _status;

    public bool IsLoaded => Status is not null;
    public bool IsAvailable => Status?.Available == true;
    public string RealTimeText => Status is null ? "–" : Status.Available && Status.RealTime ? "An" : "Aus";
    public Tone RealTimeTone => Status is null ? Tone.Neutral : Status is { Available: true, RealTime: true } ? Tone.Good : Tone.Alarm;
    public string SignatureAgeText => Status is { Available: true } s ? Labels.Days(s.SignatureAgeDays) : "–";
    public Tone SignatureTone => Status is { Available: true } s ? (s.SignatureAgeDays > 3 ? Tone.Watch : Tone.Good) : Tone.Neutral;
    public string TamperText => Status is { Available: true } s ? (s.TamperProtected ? "An" : "Aus") : "–";

    public string LastScanText
    {
        get
        {
            if (Status is not { Available: true } s) return "–";
            DateTimeOffset? last = s.LastQuickScan > s.LastFullScan || s.LastFullScan is null ? s.LastQuickScan : s.LastFullScan;
            return Labels.Date(last);
        }
    }

    public string PuaText => Status is { Available: true } s ? s.PuaMode switch { 1 => "An", 2 => "Nur melden", _ => "Aus" } : "–";
    public bool ShowPuaSuggestion => Status is { Available: true } s && s.PuaMode != 1;
    public string? DefenderNote => Status?.Note;

    public ObservableCollection<ThreatItemViewModel> Threats { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    [NotifyPropertyChangedFor(nameof(ThreatSummary), nameof(HasActiveThreats))]
    private int _activeThreatCount;

    public bool HasActiveThreats => ActiveThreatCount > 0;

    public string ThreatSummary => !IsLoaded ? "Funde werden gelesen …"
        : !IsAvailable ? "Ohne aktiven Defender gibt es keine Defender-Funde."
        : Threats.Count == 0 ? "Defender hat keine Funde gespeichert."
        : ActiveThreatCount == 0 ? "Keine aktiven Funde. Ältere Funde sind bereits behandelt."
        : ActiveThreatCount == 1 ? "1 aktiver Fund." : $"{ActiveThreatCount} aktive Funde.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var status = await _defender.GetStatusAsync();
            var threats = status.Available ? await _defender.GetThreatsAsync() : [];
            Threats.Clear();
            foreach (var t in threats.OrderByDescending(t => t.Active).ThenByDescending(t => t.Severity))
                Threats.Add(new ThreatItemViewModel(t.Name, Severity(t.Severity), t.StatusText,
                    string.Join(Environment.NewLine, t.Resources), t.Active));
            ActiveThreatCount = threats.Count(t => t.Active);
            Status = status;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Defender-Zustand nicht lesbar", ex);
            Status = new DefenderStatus(false, false, false, 0, null, 0, null, null, "Defender-Zustand konnte nicht gelesen werden.");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    static string Severity(int id) => id switch
    {
        1 => "Gering",
        2 => "Mittel",
        4 => "Hoch",
        5 => "Schwerwiegend",
        _ => "Unbekannt",
    };

    // ---- Defender operations (one at a time, cancellable) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefenderBusy))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSignaturesCommand), nameof(QuickScanCommand), nameof(FullScanCommand),
        nameof(CancelDefenderCommand), nameof(CleanCommand), nameof(CheckWithDefenderCommand), nameof(EnablePuaCommand),
        nameof(EnableRealTimeCommand))]
    private string? _defenderOperation;

    [ObservableProperty]
    private string? _defenderResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDefenderCommand))]
    private bool _canCancelDefender;

    public bool IsDefenderBusy => DefenderOperation is not null;

    bool CanRunDefender() => CanChange && !IsDefenderBusy;

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private Task UpdateSignaturesAsync() => RunDefenderAsync("Signaturen werden aktualisiert", "Signaturen aktualisiert.",
        ct => _defender.UpdateSignaturesAsync(ct));

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private Task QuickScanAsync() => RunDefenderAsync("Schnellscan läuft", "Schnellscan abgeschlossen.",
        ct => ScanWithFreshSignaturesAsync(ScanKind.Quick, "Schnellscan läuft", ct));

    [RelayCommand(CanExecute = nameof(CanRunDefender))]
    private Task FullScanAsync() => RunDefenderAsync("Vollscan läuft", "Vollscan abgeschlossen.",
        ct => ScanWithFreshSignaturesAsync(ScanKind.Full, "Vollscan läuft", ct));

    // Note added to the next result line (e.g. signatures could not be updated before a scan).
    string? _defenderNote;

    // Signatures first; if that fails the scan still runs with the signatures Defender already has.
    async Task<int> ScanWithFreshSignaturesAsync(ScanKind kind, string running, CancellationToken ct)
    {
        DefenderOperation = "Signaturen werden aktualisiert";
        try
        {
            var code = await _defender.UpdateSignaturesAsync(ct);
            if (code != 0)
                _defenderNote = $"Hinweis: Die Signaturen ließen sich nicht aktualisieren (Code {code}), gescannt wurde mit den vorhandenen.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            FileLog.Warn("Signatur-Aktualisierung vor dem Scan fehlgeschlagen", ex);
            _defenderNote = "Hinweis: Die Signaturen ließen sich nicht aktualisieren, gescannt wurde mit den vorhandenen.";
        }
        ct.ThrowIfCancellationRequested();
        DefenderOperation = running;
        return await _defender.ScanAsync(kind, ct);
    }

    [RelayCommand(CanExecute = nameof(CanCancelDefender))]
    private void CancelDefender() => _defenderCts?.Cancel();

    async Task RunDefenderAsync(string running, string done, Func<CancellationToken, Task<int>> op)
    {
        using var cts = _defenderCts = new CancellationTokenSource();
        DefenderOperation = running;
        DefenderResult = null;
        _defenderNote = null;
        CanCancelDefender = true;
        Clock.Start();
        try
        {
            var exit = await op(cts.Token);
            DefenderResult = exit == 0 ? $"{done} Dauer {Clock.Text}." : $"Defender meldet Code {exit}. Dauer {Clock.Text}.";
        }
        catch (OperationCanceledException)
        {
            DefenderResult = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Defender-Vorgang fehlgeschlagen: {running}", ex);
            DefenderResult = $"Fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            Clock.Stop();
            CanCancelDefender = false;
            DefenderOperation = null;
            _defenderCts = null;
            if (_defenderNote is not null) DefenderResult = $"{DefenderResult} {_defenderNote}";
        }
        await RefreshAsync();
    }

    public const string RestoreHint =
        "Wiederherstellen ist über Windows-Sicherheit → Schutzverlauf möglich, sofern Defender die Datei in Quarantäne verschoben hat.";

    /// <summary>Shown after a clean-up: where a quarantined file can be restored.</summary>
    [ObservableProperty]
    private string? _cleanHint;

    bool CanClean() => CanRunDefender() && !_gate.IsBusy && ActiveThreatCount > 0;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        if (!_dialogs.Confirm("Alle aktiven Funde bereinigen",
                "Defender entfernt oder isoliert die Funde. Das lässt sich nicht immer rückgängig machen.",
                "Bereinigen", Threats.Where(t => t.Active).Select(t => t.Name).ToList(), final: true))
            return;
        if (!await _gate.RunAsync(this, CleanCoreAsync)) DefenderResult = ChangeGate.BusyHint;
    }

    async Task CleanCoreAsync()
    {
        DefenderOperation = "Funde werden bereinigt";
        Clock.Start();
        try
        {
            var n = await _defender.RemoveActiveThreatsAsync();
            DefenderResult = n == 1 ? "1 Fund an Defender zur Bereinigung übergeben." : $"{n} Funde an Defender zur Bereinigung übergeben.";
            CleanHint = RestoreHint;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Bereinigen fehlgeschlagen", ex);
            DefenderResult = $"Bereinigen fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            Clock.Stop();
            DefenderOperation = null;
        }
        await RefreshAsync();
    }

    bool CanEnablePua() => CanRunDefender() && !_gate.IsBusy && ShowPuaSuggestion;

    [RelayCommand(CanExecute = nameof(CanEnablePua))]
    private async Task EnablePuaAsync()
    {
        var change = new PuaProtectionChange(_defender);
        if (!_dialogs.Confirm("PUA-Schutz einschalten",
                "Defender blockiert dann auch potenziell unerwünschte Programme, zum Beispiel Werbe-Bundles. Die Änderung steht im Verlauf und lässt sich dort zurücknehmen.",
                "Einschalten"))
            return;
        if (!await _gate.RunAsync(this, () => EnablePuaCoreAsync(change))) DefenderResult = ChangeGate.BusyHint;
    }

    async Task EnablePuaCoreAsync(PuaProtectionChange change)
    {
        DefenderOperation = "PUA-Schutz wird eingeschaltet";
        try
        {
            var finding = new Finding("pua", FindingArea.Einstellung, change.Target, change.Target,
                ["PUA-Schutz ist aus"], Rating.Info, true, change);
            var r = await _engine.ApplyAsync(change.Description, [finding], null, CancellationToken.None);
            DefenderResult = r.Applied == 1
                ? "PUA-Schutz ist eingeschaltet."
                : "PUA-Schutz konnte nicht eingeschaltet werden. Details stehen im Verlauf.";
            ChangesApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FileLog.Warn("PUA-Schutz fehlgeschlagen", ex);
            DefenderResult = $"Fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            DefenderOperation = null;
        }
        await RefreshAsync();
    }

    // ---- real-time protection off: the same fix as on the Sicherheit page (Aktion, endgültig) ----

    public bool ShowRealTimeFix => Status is { Available: true, RealTime: false } && _actions is not null && _enableRealTime is not null;

    bool CanEnableRealTime() => CanRunDefender() && !_gate.IsBusy && ShowRealTimeFix;

    // The runner confirms („endgültig“), takes the gate and logs; a blocked switch offers Windows-Sicherheit.
    [RelayCommand(CanExecute = nameof(CanEnableRealTime))]
    private async Task EnableRealTimeAsync()
    {
        if (!CanEnableRealTime()) return;
        var act = _enableRealTime!;
        // busy only once it really runs, i.e. after the confirmation and inside the gate
        var run = act with
        {
            RunAsync = (p, ct) =>
            {
                DefenderOperation = "Echtzeitschutz wird eingeschaltet";
                return act.RunAsync(p, ct);
            },
        };
        ActionOutcome? outcome;
        try
        {
            outcome = await _actions!.RunAsync(this, run, null);
        }
        finally
        {
            DefenderOperation = null;
        }
        if (outcome is null) return;
        if (outcome.Link is { } link)
        {
            if (!SystemLinks.IsAllowed(link)) FileLog.Warn($"Link nicht erlaubt: {link}");
            else if (_shell is not null && _dialogs.Confirm(act.Title, outcome.Message, SecurityViewModel.OpenSecurityText)) _shell.OpenLink(link);
        }
        await RefreshAsync();
        DefenderResult = outcome.Message;
    }

    // ---- own heuristics ----

    public ObservableCollection<HeuristicItemViewModel> HeuristicItems { get; } = [];
    public ElapsedClock HeuristicClock { get; } = new();

    [ObservableProperty]
    private string _heuristicProgress = "";

    [ObservableProperty]
    private string? _heuristicError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeuristicSummary))]
    private bool _heuristicScanned;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand), nameof(KillProcessCommand))]
    private bool _isApplying;

    public int HeuristicCount => HeuristicItems.Count;

    public string HeuristicSummary => !HeuristicScanned
        ? "Noch nicht geprüft. Die Prüfung beobachtet laufende Programme etwa 10 Sekunden lang."
        : HeuristicItems.Count == 0 ? "Keine Auffälligkeiten gefunden."
        : HeuristicItems.Count == 1 ? "1 Hinweis. Die Gründe stehen jeweils dabei."
        : $"{HeuristicItems.Count} Hinweise. Die Gründe stehen jeweils dabei.";

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task HeuristicScanAsync(CancellationToken ct)
    {
        HeuristicProgress = "Prüfung startet …";
        HeuristicError = null;
        HeuristicClock.Start();
        try
        {
            var progress = new Progress<string>(s => HeuristicProgress = s);
            var findings = await _heuristics(progress, ct);
            HeuristicItems.Clear();
            foreach (var f in findings) HeuristicItems.Add(new HeuristicItemViewModel(f));
            HeuristicScanned = true;
            HeuristicsChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            FileLog.Error("Eigene Prüfung fehlgeschlagen", ex);
            HeuristicError = $"Die eigene Prüfung ist fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            HeuristicClock.Stop();
            HeuristicProgress = "";
        }
    }

    void HeuristicsChanged()
    {
        OnPropertyChanged(nameof(HeuristicSummary));
        OnPropertyChanged(nameof(HeuristicCount));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    bool CanCheck(HeuristicItemViewModel? item) => item is not null && CanRunDefender();

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckWithDefenderAsync(HeuristicItemViewModel? item) =>
        item is null
            ? Task.CompletedTask
            : RunDefenderAsync($"Defender prüft {item.Title}", $"Defender hat {item.Title} geprüft.",
                ct => _defender.ScanPathAsync(item.Location, ct));

    bool CanDisable(HeuristicItemViewModel? item) => item is { CanDisable: true } && CanChange && !IsApplying && !_gate.IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync(HeuristicItemViewModel? item)
    {
        if (item?.Finding.Change is not { } change) return;
        if (!_dialogs.Confirm(item.DisableText,
                "Diese Änderung wird angewendet und steht danach im Verlauf, wo sie sich rückgängig machen lässt.",
                "Anwenden", [change.Description]))
            return;
        if (!await _gate.RunAsync(this, () => DisableCoreAsync(item))) _dialogs.Inform(item.DisableText, ChangeGate.BusyHint);
    }

    async Task DisableCoreAsync(HeuristicItemViewModel item)
    {
        IsApplying = true;
        try
        {
            var r = await _engine.ApplyAsync(item.DisableText, [item.Finding], null, CancellationToken.None);
            if (r.Applied == 1) HeuristicItems.Remove(item);
            _dialogs.Inform("Zusammenfassung",
                r.Applied == 1 ? "Die Änderung ist angewendet." : "Die Änderung ist fehlgeschlagen. Details stehen im Verlauf.",
                [r.RestorePoint.Message]);
            HeuristicsChanged();
            ChangesApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FileLog.Error("Heuristik-Aktion fehlgeschlagen", ex);
            _dialogs.Inform("Fehlgeschlagen", ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    bool CanKill(HeuristicItemViewModel? item) => item is { CanKill: true } && CanChange && !IsApplying && !_gate.IsBusy;

    [RelayCommand(CanExecute = nameof(CanKill))]
    private async Task KillProcessAsync(HeuristicItemViewModel? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm("Prozess beenden",
                "Alle laufenden Prozesse dieser Programmdatei werden beendet. Das ist nicht umkehrbar: Ungespeicherte Daten des Programms gehen verloren. Die Datei selbst bleibt unverändert.",
                "Beenden", [item.Location], final: true))
            return;
        if (!await _gate.RunAsync(this, () => KillCoreAsync(item))) _dialogs.Inform("Prozess beenden", ChangeGate.BusyHint);
    }

    async Task KillCoreAsync(HeuristicItemViewModel item)
    {
        IsApplying = true;
        try
        {
            var n = await Task.Run(() => _killByPath(item.Location));
            var text = n switch
            {
                0 => "Es lief kein passender Prozess mehr.",
                1 => "1 Prozess beendet.",
                _ => $"{n} Prozesse beendet.",
            };
            // Either way nothing is running now: hide the kill button and say so on the card.
            item.ProcessNote = n == 0 ? "Läuft nicht mehr." : $"{text} Startet das Programm neu, erscheint es bei der nächsten Prüfung wieder.";
            KillProcessCommand.NotifyCanExecuteChanged();
            _dialogs.Inform("Prozess beenden", text);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Prozess beenden fehlgeschlagen", ex);
            _dialogs.Inform("Prozess beenden", $"Fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            IsApplying = false;
        }
    }
}
