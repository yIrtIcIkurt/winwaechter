using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Changes;
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
    public const string OwnEntryHintText = "Das ist WinWächter selbst – bitte über die Windows-Einstellungen deinstallieren.";

    /// <param name="appDir">The app's own folder (default: AppContext.BaseDirectory), to recognise its own entry.</param>
    public ProgramItemViewModel(InstalledProgram p, string? appDir = null)
    {
        Program = p;
        IsOwnEntry = OwnEntry.Is(p, appDir ?? AppContext.BaseDirectory);
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
    /// <summary>WinWaechter's own entry: treated like a protected one.</summary>
    public bool IsOwnEntry { get; }
    public bool CanUninstall => Program.Flag != ProgramFlag.Protected && !IsOwnEntry;
    public string? ProtectedHint => IsOwnEntry ? OwnEntryHintText : CanUninstall ? null : ProtectedHintText;

    /// <summary>Herausgeber · Version · Datum · Größe, empty parts left out.</summary>
    public string Details => string.Join(" · ",
        new[] { Publisher, Version, DateText, SizeText }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private bool _canForceRemove;

    /// <summary>„Erzwungen entfernen“ offered; never for protected entries or WinWaechter itself.</summary>
    public bool CanForceRemove
    {
        get => _canForceRemove;
        set => SetProperty(ref _canForceRemove, value && CanUninstall);
    }
}

/// <summary>One removable preinstalled app: title, recommendation and what the Verlauf can do afterwards.</summary>
public sealed partial class BloatAppItemViewModel : ObservableObject
{
    readonly Action? _changed;

    public BloatAppItemViewModel(BloatAppRow row, bool canSelect, Action? changed = null)
    {
        Row = row;
        CanSelect = canSelect;
        _changed = changed;
        UndoText = row.Change.UndoLimit is null ? "Rückgängig im Verlauf" : UndoLimits.StoreOnly;
    }

    public BloatAppRow Row { get; }
    public string Title => Row.Rule.Title;
    public bool IsRecommended => Row.Rule.Recommended;
    public bool CanSelect { get; }
    public string UndoText { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed?.Invoke();
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
    public const string AsUserText = "Läuft mit Ihren normalen Benutzerrechten, nicht mit Adminrechten.";
    public const string Irreversible =
        "Das Deinstallationsprogramm des Herstellers wird gestartet. Das lässt sich in WinWächter nicht rückgängig machen.";
    public const string RestartText = "Ein Neustart ist nötig, um die Deinstallation abzuschließen.";
    public const string ForcedText = "Alles wird gesichert und lässt sich im Verlauf rückgängig machen.";
    public const string ReadingText = "Programme werden gelesen …";
    public const string DoneQuestion = "Ist die Deinstallation abgeschlossen?";
    public const string ChildrenText = "Das Deinstallationsprogramm hat weitere Programme geöffnet (z. B. Browser).";

    readonly IProgramActions _actions;
    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly ChangeGate _gate;
    readonly Func<IReadOnlyList<BloatAppRow>>? _apps;
    readonly bool _sameAccount;

    List<ProgramItemViewModel> _rows = [];
    // why „Erzwungen entfernen“ was offered per program id; survives a reload
    readonly Dictionary<string, (bool Missing, bool Finished)> _forceOffers = new(StringComparer.OrdinalIgnoreCase);
    InstalledProgram? _pending;
    IReadOnlyList<InstalledProgram>? _leftoversFor;

    public ProgramsViewModel(IProgramActions actions, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell,
        bool canChange, ChangeGate gate, Func<IReadOnlyList<BloatAppRow>>? apps = null, bool sameAccount = true)
    {
        _apps = apps;
        _sameAccount = sameAccount;
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
    [NotifyCanExecuteChangedFor(nameof(RemoveAppsCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallUnwantedCommand))]
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
    public string? LeftoversTitle => _leftoversFor switch
    {
        null or [] => null,
        [var p] => $"Reste von „{p.DisplayName}“",
        var all => $"Reste von {all.Count} Programmen",
    };
    public bool ShowLeftovers => Leftovers.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyLeftoversCommand))]
    private int _selectedLeftoverCount;

    // ---------- preinstalled apps ----------

    public const string OtherAccountText =
        "WinWächter läuft unter einem anderen Konto als dem angemeldeten. Apps lassen sich nur im eigenen Konto entfernen.";

    public ObservableCollection<BloatAppItemViewModel> Apps { get; } = [];
    public bool ShowApps => Apps.Count > 0;

    /// <summary>Removal is locked: no admin rights, or the process runs as another account than the signed-in user.</summary>
    public bool AppsLocked => !CanChange || !_sameAccount;
    public string? AppsLockedText => !CanChange ? AdminNotice : !_sameAccount ? OtherAccountText : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAppsCommand))]
    private int _selectedAppCount;

    public string RemoveAppsText => $"Ausgewählte entfernen ({SelectedAppCount})";
    partial void OnSelectedAppCountChanged(int value) => OnPropertyChanged(nameof(RemoveAppsText));

    void UpdateAppSelection() => SelectedAppCount = Apps.Count(a => a.IsSelected);

    // The scan runs on the pool; a failure leaves the programs list working and the section empty.
    async Task LoadAppsAsync()
    {
        IReadOnlyList<BloatAppRow> rows = [];
        if (_apps is { } read)
        {
            try { rows = await Task.Run(() => read()); }
            catch (Exception ex) { FileLog.Error("Vorinstallierte Apps lesen fehlgeschlagen", ex); }
        }
        Apps.Clear();
        foreach (var r in rows) Apps.Add(new BloatAppItemViewModel(r, !AppsLocked, UpdateAppSelection));
        UpdateAppSelection();
        OnPropertyChanged(nameof(ShowApps));
        SelectRecommendedAppsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectApps))]
    private void SelectRecommendedApps()
    {
        foreach (var a in Apps) a.IsSelected = a.IsRecommended;
    }

    bool CanSelectApps() => !AppsLocked && Apps.Any(a => a.IsRecommended);

    [RelayCommand(CanExecute = nameof(CanRemoveApps))]
    private async Task RemoveAppsAsync()
    {
        if (!CanRemoveApps()) return;
        var selected = Apps.Where(a => a.IsSelected).ToList();
        var question = selected.Count == 1
            ? "Diese App wird für Ihr Konto entfernt. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich zurücknehmen, was möglich ist."
            : $"Diese {selected.Count} Apps werden für Ihr Konto entfernt. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich zurücknehmen, was möglich ist.";
        if (!_dialogs.Confirm("Vorinstallierte Apps entfernen", question, "Entfernen",
                selected.Select(a => ChangeText.ConfirmLine(a.Row.Change)).ToList()))
            return;

        var findings = selected.Select(a => new Finding($"app|{a.Row.Package.FamilyName}", FindingArea.Einstellung,
            a.Row.Change.Description, a.Row.Package.FamilyName, [], Rating.Info, false, a.Row.Change)).ToList();
        var ran = await _gate.RunAsync(this, async () =>
        {
            if (await ApplyAsync("Vorinstallierte Apps entfernen", findings) is null) return;
            await ReloadAsync();
        });
        if (!ran) LastSummary = ChangeGate.BusyHint;
    }

    bool CanRemoveApps() => CanAct() && !AppsLocked && SelectedAppCount > 0;

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
        await LoadAppsAsync();
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
        OnPropertyChanged(nameof(UnwantedCount));
        OnPropertyChanged(nameof(ShowUninstallUnwanted));
        OnPropertyChanged(nameof(UninstallUnwantedText));
        NotifyActions();
    }

    // ---------- official uninstall ----------

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync(ProgramItemViewModel? item)
    {
        if (item is null || !CanUninstall(item)) return;
        var p = item.Program;
        if (!await _gate.RunAsync(this, async () =>
            {
                if (await RunUninstallAsync(p, confirm: true)) await SearchLeftoversAsync([p]);
            }))
            LastSummary = ChangeGate.BusyHint;
    }

    bool CanUninstall(ProgramItemViewModel? item) => CanAct() && item is { CanUninstall: true };

    /// <summary>The vendor's uninstaller for one program, with every rule of the single uninstall. True when the program
    /// is gone afterwards (the caller then searches leftovers). confirm false skips only the per-program question,
    /// because the caller already asked once for all; the uninstaller and its signature are logged instead.</summary>
    async Task<bool> RunUninstallAsync(InstalledProgram p, bool confirm)
    {
        IsBusy = true;
        LastSummary = null;
        try
        {
            var plan = await Task.Run(() => _actions.PlanUninstall(p));
            if (plan.Route == UninstallRoute.OpenSettings)
            {
                ViaSettings(p, plan);
                return false;
            }

            var sig = await Task.Run(() => _actions.UninstallerSignature(plan));
            var cmd = plan.Command!;
            var lines = new List<string> { $"Programm: {cmd.Exe} {cmd.Arguments}".TrimEnd(), SignatureText.Line(sig) };
            if (plan.Route == UninstallRoute.LaunchAsUser) lines.Add(AsUserText);
            if (confirm)
            {
                var question = $"„{p.DisplayName}“ von {p.Publisher ?? "unbekanntem Herausgeber"}. {Irreversible}";
                if (!_dialogs.Confirm("Deinstallieren", question, "Deinstallieren", lines, final: true)) return false;
            }
            else FileLog.Info($"Deinstallation „{p.DisplayName}“: {string.Join(" · ", lines)}");

            ProgressText = RunningText;
            UninstallOutcome outcome;
            try
            {
                // CreateProcess and the job setup never run on the UI thread
                outcome = await Task.Run(() => _actions.RunUninstallerAsync(plan));
            }
            catch (UserLaunchException ex)
            {
                // nothing was started: the user removes it via the Windows settings, as before
                FileLog.Warn($"Deinstallation mit Benutzerrechten nicht möglich: {p.DisplayName}", ex);
                ProgressText = "";
                ViaSettings(p, plan with { Route = UninstallRoute.OpenSettings, Command = null, Reason = SettingsReason(p, ex) });
                return false;
            }
            ProgressText = "";
            if (outcome.Cancelled)
            {
                LastSummary = "Deinstallation abgebrochen.";
                return false;
            }
            var summary = outcome.ExitCode == 0 || outcome.RestartRequired
                ? "Das Deinstallationsprogramm ist beendet."
                : $"Das Deinstallationsprogramm ist beendet (Rückgabewert {outcome.ExitCode}).";
            LastSummary = outcome.RestartRequired ? $"{summary} {RestartText}" : summary;

            var doneQuestion = outcome.ChildrenStillRunning ? $"{ChildrenText} {DoneQuestion}" : DoneQuestion;
            // one after the other: the question names the program it is about
            var askTitle = confirm ? "Deinstallation" : $"Deinstallation „{p.DisplayName}“";
            if (!_dialogs.Ask(askTitle, doneQuestion, "Ja", "Noch nicht"))
            {
                Pending = p;
                return false;
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
                return false;
            }

            if (Pending is { } pending && SameEntry(pending, p)) Pending = null;
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Error($"Deinstallation fehlgeschlagen: {p.DisplayName}", ex);
            _dialogs.Inform("Deinstallation fehlgeschlagen", ex.Message);
            return false;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // ---------- all unwanted programs at once (Aktion, endgültig) ----------

    public const string UnwantedTitle = "Alle unerwünschten entfernen";

    /// <summary>Programs flagged „Unerwünscht“ or „Miner“ that may be uninstalled (never protected ones or WinWächter itself).</summary>
    public int UnwantedCount => UnwantedPrograms().Count;
    public bool ShowUninstallUnwanted => UnwantedCount > 0;
    public string UninstallUnwantedText => $"{UnwantedTitle} ({UnwantedCount})";

    List<InstalledProgram> UnwantedPrograms() => _rows
        .Where(r => r.Program.Flag is ProgramFlag.Pua or ProgramFlag.Miner && r.CanUninstall)
        .OrderBy(r => r.Name, StringComparer.Create(Labels.De, ignoreCase: true))
        .Select(r => r.Program)
        .ToList();

    bool CanUninstallUnwanted() => CanAct() && UnwantedCount > 0;

    // One question for the whole list, then one after the other through the normal uninstall (signature, user rights,
    // „Ist die Deinstallation abgeschlossen?“) inside one gate, and one leftover search over all removed programs.
    [RelayCommand(CanExecute = nameof(CanUninstallUnwanted))]
    private async Task UninstallUnwantedAsync()
    {
        if (!CanUninstallUnwanted()) return;
        var targets = UnwantedPrograms();
        if (!_dialogs.Confirm(UnwantedTitle, $"{Irreversible} {ActionRunner.FinalNote}", "Deinstallieren",
                targets.Select(p => p.DisplayName).ToList(), final: true))
            return;

        var ran = await _gate.RunAsync(this, async () =>
        {
            FileLog.Info($"Aktion: {UnwantedTitle} ({targets.Count})");
            var removed = new List<InstalledProgram>();
            var restart = false;
            foreach (var p in targets)
            {
                var gone = await RunUninstallAsync(p, confirm: false);
                restart |= LastSummary?.Contains(RestartText, StringComparison.Ordinal) == true;
                FileLog.Info($"{UnwantedTitle}: „{p.DisplayName}“ {(gone ? "entfernt" : "nicht entfernt")}");
                if (gone) removed.Add(p);
            }
            if (removed.Count > 0) await SearchLeftoversAsync(removed);
            var summary = $"{removed.Count} von {targets.Count} unerwünschten Programmen entfernt.";
            LastSummary = restart ? $"{summary} {RestartText}" : summary;
        });
        if (!ran) LastSummary = ChangeGate.BusyHint;
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

    // "liegt in einem Ordner, den auch Programme ohne Adminrechte ändern können" is only said about HKLM entries
    // (their uninstaller is in such a folder, else it would have run elevated); per-user programs get the plain text.
    internal static string SettingsReason(InstalledProgram p, UserLaunchException ex) =>
        ex.Reason == UninstallPlanner.NeedsAdminReason && !p.IsMachineEntry
            ? "Das Deinstallationsprogramm verlangt Adminrechte, das Programm wurde aber für Ihr Benutzerkonto installiert."
            : ex.Reason;

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
            await ShowLeftoversAsync([p]);
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

    // after an uninstall from this page (one program or the unwanted ones): the gate is already held
    async Task SearchLeftoversAsync(IReadOnlyList<InstalledProgram> removed)
    {
        IsBusy = true;
        try
        {
            await ShowLeftoversAsync(removed);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Restesuche fehlgeschlagen: {string.Join(", ", removed.Select(p => p.DisplayName))}", ex);
            _dialogs.Inform("Restesuche fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // one search per removed program, shown together; a trace found for two programs is listed once
    async Task ShowLeftoversAsync(IReadOnlyList<InstalledProgram> removed)
    {
        ProgressText = "Reste werden gesucht …";
        var found = await Task.Run(() => removed.SelectMany(p => _actions.FindLeftovers(p))
            .DistinctBy(f => f.Id, StringComparer.Ordinal).ToList());
        if (found.Count == 0)
        {
            _dialogs.Inform("Reste", "Keine Reste gefunden.");
            return;
        }
        Leftovers.Clear();
        _leftoversFor = removed;
        OnPropertyChanged(nameof(LeftoversTitle));
        // folders come unselected, orphaned entries preselected (Finding.Preselected)
        foreach (var f in found) Leftovers.Add(new FindingItemViewModel(f, UpdateLeftoverSelection));
    }

    [RelayCommand(CanExecute = nameof(CanApplyLeftovers))]
    private async Task ApplyLeftoversAsync()
    {
        if (_leftoversFor is not { Count: > 0 } removed || !CanApplyLeftovers()) return;
        var selected = Leftovers.Where(i => i.IsSelected && i.CanSelect).ToList();
        var question = selected.Count == 1
            ? "Diese Änderung wird angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sie sich rückgängig machen."
            : $"Diese {selected.Count} Änderungen werden angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lassen sie sich rückgängig machen.";
        if (!_dialogs.Confirm(LeftoversTitle!, question, "Anwenden", selected.Select(i => i.ChangeText ?? i.Title).ToList())) return;

        var ran = await _gate.RunAsync(this, async () =>
        {
            var title = removed.Count == 1 ? $"Reste von {removed[0].DisplayName}" : $"Reste von {removed.Count} Programmen";
            var result = await ApplyAsync(title, selected.Select(i => i.Finding).ToList());
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
        CanAct() && item is { CanForceRemove: true, CanUninstall: true } && !item.Program.WindowsInstaller;

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
        RemoveAppsCommand.NotifyCanExecuteChanged();
        UninstallUnwantedCommand.NotifyCanExecuteChanged();
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
