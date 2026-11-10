using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>One installed program: name, details, flag chip and its actions.</summary>
public sealed partial class ProgramItemViewModel : ObservableObject
{
    public const string ProtectedHintText = "Geschützt – bei Bedarf über Windows-Einstellungen → Apps deinstallieren";

    public ProgramItemViewModel(InstalledProgram p)
    {
        Program = p;
        DateText = p.InstallDate is { } d ? d.ToString("dd.MM.yyyy", Labels.De) : "";
        SizeText = p.SizeBytes is { } b ? Labels.Size(b) : "";
        (FlagText, FlagTone) = p.Flag switch
        {
            ProgramFlag.Protected => ("Geschützt", Tone.Good),
            ProgramFlag.Pua => ("Unerwünscht", Tone.Watch),
            ProgramFlag.Miner => ("Miner", Tone.Alarm),
            _ => ((string?)null, Tone.Neutral),
        };
    }

    public InstalledProgram Program { get; }
    public string Name => Program.DisplayName;
    public string? Publisher => Program.Publisher;
    public string? Version => Program.Version;
    public string DateText { get; }
    public string SizeText { get; }
    public string? FlagText { get; }
    public Tone FlagTone { get; }
    public bool CanUninstall => Program.Flag != ProgramFlag.Protected;
    public string? ProtectedHint => CanUninstall ? null : ProtectedHintText;

    /// <summary>Herausgeber · Version · Datum · Größe, empty parts left out.</summary>
    public string Details => string.Join(" · ",
        new[] { Publisher, Version, DateText, SizeText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [ObservableProperty]
    private bool _canForceRemove;
}

public enum ProgramSort { Name, Size, Date }

public sealed record ProgramSortOption(ProgramSort Value, string Label);

/// <summary>
/// „Programme“: installed programs with search and sort, the vendor's uninstaller (not undoable),
/// the emergency „Erzwungen entfernen“ and the leftover search after an uninstall.
/// </summary>
public sealed partial class ProgramsViewModel : ObservableObject
{
    public const string RunningText =
        "Deinstallation läuft … Sie lässt sich hier nicht abbrechen. Bitte folgen Sie dem Fenster des Deinstallationsprogramms.";
    public const string Irreversible =
        "Das Deinstallationsprogramm des Herstellers wird gestartet. Das lässt sich in WinWächter nicht rückgängig machen.";
    public const string RestartText = "Ein Neustart ist nötig, um die Deinstallation abzuschließen.";
    public const string ForcedText = "Alles wird gesichert und lässt sich im Verlauf rückgängig machen.";
    public const string ReadingText = "Programme werden gelesen …";

    readonly IProgramActions _actions;
    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly ChangeGate _gate;

    List<ProgramItemViewModel> _rows = [];
    // why „Erzwungen entfernen“ was offered per program id; survives a reload
    readonly Dictionary<string, (bool Missing, bool Finished)> _forceOffers = new(StringComparer.OrdinalIgnoreCase);
    InstalledProgram? _pending;
    InstalledProgram? _leftoversFor;

    public ProgramsViewModel(IProgramActions actions, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell,
        bool canChange, ChangeGate gate)
    {
        _actions = actions;
        _engine = engine;
        _dialogs = dialogs;
        _shell = shell;
        CanChange = canChange;
        _gate = gate;
        gate.PropertyChanged += (_, _) =>
        {
            NotifyActions();
            OnPropertyChanged(nameof(GateHint));
        };
        Leftovers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowLeftovers));
            UpdateLeftoverSelection();
        };
    }

    /// <summary>Raised after changes were applied (the history should reload).</summary>
    public event EventHandler? ChangesApplied;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    /// <summary>True once the list was read (the shell reads it on the first visit).</summary>
    public bool HasLoaded { get; private set; }

    public ObservableCollection<ProgramItemViewModel> Items { get; } = [];

    public IReadOnlyList<ProgramSortOption> SortOptions { get; } =
    [
        new(ProgramSort.Name, "Name"), new(ProgramSort.Size, "Größe"), new(ProgramSort.Date, "Installationsdatum"),
    ];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ProgramSort _sort = ProgramSort.Name;

    partial void OnSearchTextChanged(string value) => UpdateView();
    partial void OnSortChanged(ProgramSort value) => UpdateView();

    public string CountText => $"{Items.Count} von {_rows.Count} Programmen";

    /// <summary>Read, but nothing to show (no programs or no search match).</summary>
    public bool IsEmpty => HasLoaded && Items.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceRemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindLeftoversCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyLeftoversCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string? _lastSummary;

    /// <summary>Program the user uninstalls outside WinWächter (Windows-Einstellungen, or „Noch nicht“).
    /// The setter is internal only so tests can stage a page state.</summary>
    public InstalledProgram? Pending
    {
        get => _pending;
        internal set
        {
            if (!SetProperty(ref _pending, value)) return;
            OnPropertyChanged(nameof(PendingText));
            OnPropertyChanged(nameof(ShowPending));
            FindLeftoversCommand.NotifyCanExecuteChanged();
        }
    }

    public string? PendingText => Pending is { } p ? $"Nach der Deinstallation von „{p.DisplayName}“ lassen sich Reste suchen." : null;
    public bool ShowPending => Pending is not null;

    public ObservableCollection<FindingItemViewModel> Leftovers { get; } = [];
    public string? LeftoversTitle => _leftoversFor is { } p ? $"Reste von „{p.DisplayName}“" : null;
    public bool ShowLeftovers => Leftovers.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyLeftoversCommand))]
    private int _selectedLeftoverCount;

    // ---------- list ----------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ProgressText = ReadingText;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            FileLog.Error("Programme lesen fehlgeschlagen", ex);
            LastSummary = $"Lesen fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanRefresh() => !IsBusy;

    async Task LoadAsync()
    {
        var programs = await Task.Run(() => _actions.ReadAll());
        _rows = programs.Select(p =>
        {
            var row = new ProgramItemViewModel(p);
            row.CanForceRemove = _forceOffers.TryGetValue(p.Id, out var o) && ForcedRemovalPlanner.IsOffered(p, o.Missing, o.Finished);
            return row;
        }).ToList();
        HasLoaded = true;
        UpdateView();
    }

    void UpdateView()
    {
        var q = SearchText.Trim();
        var shown = _rows.Where(r => q.Length == 0
            || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (r.Publisher?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        var byName = StringComparer.Create(Labels.De, ignoreCase: true);
        shown = Sort switch
        {
            ProgramSort.Size => shown.OrderBy(r => r.Program.SizeBytes is null).ThenByDescending(r => r.Program.SizeBytes)
                .ThenBy(r => r.Name, byName),
            ProgramSort.Date => shown.OrderBy(r => r.Program.InstallDate is null).ThenByDescending(r => r.Program.InstallDate)
                .ThenBy(r => r.Name, byName),
            _ => shown.OrderBy(r => r.Name, byName),
        };
        Items.Clear();
        foreach (var r in shown) Items.Add(r);
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyActions();
    }

    // ---------- official uninstall ----------

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync(ProgramItemViewModel? item)
    {
        if (item is null || !CanUninstall(item)) return;
        if (!await _gate.RunAsync(this, () => RunUninstallAsync(item.Program)))
            LastSummary = ChangeGate.BusyHint;
    }

    bool CanUninstall(ProgramItemViewModel? item) => CanAct() && item is { CanUninstall: true };

    async Task RunUninstallAsync(InstalledProgram p)
    {
        IsBusy = true;
        LastSummary = null;
        try
        {
            var plan = await Task.Run(() => _actions.PlanUninstall(p));
            if (plan.Route == UninstallRoute.OpenSettings)
            {
                ViaSettings(p, plan);
                return;
            }

            var sig = await Task.Run(() => _actions.UninstallerSignature(plan));
            var cmd = plan.Command!;
            var lines = new List<string> { $"Programm: {cmd.Exe} {cmd.Arguments}".TrimEnd(), SignatureText.Line(sig) };
            var question = $"„{p.DisplayName}“ von {p.Publisher ?? "unbekanntem Herausgeber"}. {Irreversible}";
            if (!_dialogs.Confirm("Deinstallieren", question, "Deinstallieren", lines, final: true)) return;

            ProgressText = RunningText;
            // CreateProcess and the job setup never run on the UI thread
            var outcome = UninstallExit.Interpret(await Task.Run(() => _actions.RunUninstallerAsync(plan)));
            ProgressText = "";
            if (outcome.Cancelled)
            {
                LastSummary = "Deinstallation abgebrochen.";
                return;
            }
            var summary = outcome.ExitCode == 0 || outcome.RestartRequired
                ? "Das Deinstallationsprogramm ist beendet."
                : $"Das Deinstallationsprogramm ist beendet (Rückgabewert {outcome.ExitCode}).";
            LastSummary = outcome.RestartRequired ? $"{summary} {RestartText}" : summary;

            if (!_dialogs.Ask("Deinstallation", "Ist die Deinstallation abgeschlossen?", "Ja", "Noch nicht"))
            {
                Pending = p;
                return;
            }

            ProgressText = ReadingText;
            await LoadAsync();
            if (await Task.Run(() => _actions.IsStillListed(p)))
            {
                var now = _rows.FirstOrDefault(r => SameEntry(r.Program, p))?.Program ?? p;
                var offered = ForcedRemovalPlanner.IsOffered(now, uninstallerMissing: false, finishedButStillListed: true);
                if (offered) OfferForce(now, missing: false, finished: true);
                _dialogs.Inform("Deinstallation", offered
                    ? "Der Eintrag ist noch vorhanden. Wenn das Programm trotzdem entfernt ist, räumt „Erzwungen entfernen“ den Rest auf; das lässt sich im Verlauf rückgängig machen."
                    : "Der Eintrag ist noch vorhanden. Bitte prüfen Sie das Programm in den Windows-Einstellungen.");
                return;
            }

            if (Pending is { } pending && SameEntry(pending, p)) Pending = null;
            await ShowLeftoversAsync(p);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Deinstallation fehlgeschlagen: {p.DisplayName}", ex);
            _dialogs.Inform("Deinstallation fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // HKU entries, user-writable or unsafe uninstallers: the user removes the program via Windows-Einstellungen
    void ViaSettings(InstalledProgram p, UninstallPlan plan)
    {
        if (plan.UninstallerMissing && ForcedRemovalPlanner.IsOffered(p, uninstallerMissing: true, finishedButStillListed: false))
            OfferForce(p, missing: true, finished: false);
        if (!_dialogs.Confirm("Über Windows-Einstellungen deinstallieren",
                $"{plan.Reason} Bitte deinstallieren Sie „{p.DisplayName}“ über die Windows-Einstellungen.", "Einstellungen öffnen"))
            return;
        _shell.OpenLink(SystemLinks.AppsFeatures);
        Pending = p;
    }

    void OfferForce(InstalledProgram p, bool missing, bool finished)
    {
        _forceOffers[p.Id] = (missing, finished);
        foreach (var r in _rows.Where(r => SameEntry(r.Program, p))) r.CanForceRemove = true;
        ForceRemoveCommand.NotifyCanExecuteChanged();
    }

    static bool SameEntry(InstalledProgram a, InstalledProgram b) => string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    // ---------- leftovers ----------

    [RelayCommand(CanExecute = nameof(CanFindLeftovers))]
    private async Task FindLeftoversAsync()
    {
        if (Pending is not { } p || !CanFindLeftovers()) return;
        IsBusy = true;
        try
        {
            if (await Task.Run(() => _actions.IsStillListed(p)))
            {
                _dialogs.Inform("Reste", "Das Programm ist noch installiert. Reste lassen sich erst nach der Deinstallation suchen.");
                return;
            }
            ProgressText = ReadingText;
            await LoadAsync();
            await ShowLeftoversAsync(p);
            Pending = null;   // only once the search ran; a failure keeps the banner for another try
        }
        catch (Exception ex)
        {
            FileLog.Error($"Restesuche fehlgeschlagen: {p.DisplayName}", ex);
            _dialogs.Inform("Restesuche fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanFindLeftovers() => CanChange && !IsBusy && Pending is not null;

    async Task ShowLeftoversAsync(InstalledProgram p)
    {
        ProgressText = "Reste werden gesucht …";
        var found = await Task.Run(() => _actions.FindLeftovers(p));
        if (found.Count == 0)
        {
            _dialogs.Inform("Reste", "Keine Reste gefunden.");
            return;
        }
        Leftovers.Clear();
        _leftoversFor = p;
        OnPropertyChanged(nameof(LeftoversTitle));
        // folders come unselected, orphaned entries preselected (Finding.Preselected)
        foreach (var f in found) Leftovers.Add(new FindingItemViewModel(f, UpdateLeftoverSelection));
    }

    [RelayCommand(CanExecute = nameof(CanApplyLeftovers))]
    private async Task ApplyLeftoversAsync()
    {
        if (_leftoversFor is not { } p || !CanApplyLeftovers()) return;
        var selected = Leftovers.Where(i => i.IsSelected && i.CanSelect).ToList();
        var question = selected.Count == 1
            ? "Diese Änderung wird angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sie sich rückgängig machen."
            : $"Diese {selected.Count} Änderungen werden angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lassen sie sich rückgängig machen.";
        if (!_dialogs.Confirm(LeftoversTitle!, question, "Anwenden", selected.Select(i => i.ChangeText ?? i.Title).ToList())) return;

        var ran = await _gate.RunAsync(this, async () =>
        {
            var result = await ApplyAsync($"Reste von {p.DisplayName}", selected.Select(i => i.Finding).ToList());
            if (result is null) return;
            // the engine applies in order and stops at the first failure
            foreach (var done in selected.Take(result.Applied)) Leftovers.Remove(done);
            if (Leftovers.Count == 0) CloseLeftovers();
        });
        if (!ran) LastSummary = ChangeGate.BusyHint;
    }

    bool CanApplyLeftovers() => CanAct() && SelectedLeftoverCount > 0;

    [RelayCommand]
    private void CloseLeftovers()
    {
        Leftovers.Clear();
        _leftoversFor = null;
        OnPropertyChanged(nameof(LeftoversTitle));
    }

    void UpdateLeftoverSelection() => SelectedLeftoverCount = Leftovers.Count(i => i.IsSelected && i.CanSelect);

    // ---------- forced removal ----------

    [RelayCommand(CanExecute = nameof(CanForceRemove))]
    private async Task ForceRemoveAsync(ProgramItemViewModel? item)
    {
        if (item is null || !CanForceRemove(item)) return;
        var p = item.Program;

        ForcedRemovalPlan plan;
        IsBusy = true;
        ProgressText = "Änderungen werden ermittelt …";
        try
        {
            plan = await Task.Run(() => _actions.PlanForcedRemoval(p));
        }
        catch (Exception ex)
        {
            FileLog.Error($"Erzwungenes Entfernen nicht planbar: {p.DisplayName}", ex);
            _dialogs.Inform("Erzwungen entfernen", ex.Message);
            return;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }

        if (plan.Changes.Count == 0)
        {
            _dialogs.Inform("Erzwungen entfernen", $"Für „{p.DisplayName}“ gibt es nichts zu entfernen.", plan.Notes);
            return;
        }
        // every single change is listed before anything happens
        var items = plan.Changes.Select(c => c.Description).Concat(plan.Notes).ToList();
        if (!_dialogs.Confirm("Erzwungen entfernen", ForcedText, "Entfernen", items)) return;

        var findings = plan.Changes
            .Select(c => new Finding($"forced|{c.Target}", AreaOf(c.Kind), c.Description, c.Target, [], Rating.Info, true, c))
            .ToList();
        var ran = await _gate.RunAsync(this, async () =>
        {
            if (await ApplyAsync($"Erzwungen entfernt: {p.DisplayName}", findings) is null) return;
            _forceOffers.Remove(p.Id);
            await ReloadAsync();
        });
        if (!ran) LastSummary = ChangeGate.BusyHint;
    }

    bool CanForceRemove(ProgramItemViewModel? item) =>
        CanAct() && item is { CanForceRemove: true } && item.Program.Flag != ProgramFlag.Protected && !item.Program.WindowsInstaller;

    static FindingArea AreaOf(string kind) => kind switch
    {
        "service" => FindingArea.Dienst,
        "task" => FindingArea.Aufgabe,
        "autostart" => FindingArea.Autostart,
        "folder" or "file" => FindingArea.Speicher,
        _ => FindingArea.Einstellung,
    };

    // ---------- shared ----------

    bool CanAct() => CanChange && !IsBusy && !_gate.IsBusy;

    void NotifyActions()
    {
        UninstallCommand.NotifyCanExecuteChanged();
        ForceRemoveCommand.NotifyCanExecuteChanged();
        ApplyLeftoversCommand.NotifyCanExecuteChanged();
        FindLeftoversCommand.NotifyCanExecuteChanged();
    }

    async Task ReloadAsync()
    {
        IsBusy = true;
        ProgressText = ReadingText;
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            FileLog.Error("Programme lesen fehlgeschlagen", ex);
            LastSummary = $"Lesen fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // applies through the engine (journal, history), shows the summary; null when it failed before any change
    async Task<ApplyResult?> ApplyAsync(string title, IReadOnlyList<Finding> findings)
    {
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync(title, findings, progress, CancellationToken.None);

            var notDone = findings.Count - result.Applied - result.Failed;
            var lines = new List<string> { $"Angewendet: {result.Applied}", $"Fehlgeschlagen: {result.Failed}" };
            if (notDone > 0) lines.Add($"Nicht ausgeführt: {notDone}");
            lines.Add(result.RestorePoint.Message);

            string message;
            if (result.Failed > 0)
            {
                message = "Eine Änderung ist fehlgeschlagen, danach wurde angehalten. Details stehen im Verlauf.";
                LastSummary = $"Zuletzt: {result.Applied} angewendet, {result.Failed} fehlgeschlagen.";
            }
            else
            {
                message = "Die Änderungen sind angewendet und stehen im Verlauf.";
                LastSummary = $"Zuletzt: {result.Applied} angewendet.";
            }
            _dialogs.Inform("Zusammenfassung", message, lines);
            ChangesApplied?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Error($"Anwenden fehlgeschlagen: {title}", ex);
            _dialogs.Inform("Anwenden fehlgeschlagen", ex.Message);
            return null;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }
}
