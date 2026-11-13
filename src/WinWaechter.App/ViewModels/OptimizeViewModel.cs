using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;

namespace WinWaechter.App.ViewModels;

/// <summary>"Optimieren": scan, grouped preview, apply selected changes.</summary>
public sealed partial class OptimizeViewModel : ObservableObject
{
    static readonly (FindingArea Area, string Title)[] Areas =
    [
        (FindingArea.Autostart, "Autostart"),
        (FindingArea.Dienst, "Dienste"),
        (FindingArea.Aufgabe, "Aufgaben"),
        (FindingArea.Speicher, "Speicher"),
    ];

    // after the area headings; these collect findings by Finding.Group regardless of their area
    static readonly string[] GroupOrder =
    [
        FindingGroups.BrowserCache, FindingGroups.WindowsRemnants, FindingGroups.OldInstallers,
        FindingGroups.OrphanEntries, FindingGroups.OrphanFolders, FindingGroups.ContextMenu,
    ];

    /// <summary>Explains the Rechtsklick-Menü group: what the list cannot show.</summary>
    public const string ContextMenuHint =
        "Windows 11 zeigt viele dieser Einträge erst unter „Weitere Optionen anzeigen“. Einträge aus App-Paketen (neues Menü) lassen sich so nicht abschalten und fehlen hier.";

    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly List<FindingItemViewModel> _all = [];

    readonly ChangeGate _gate;
    readonly ActionRunner? _actions;
    readonly SystemAction? _restartExplorer;

    public OptimizeViewModel(OptimizerEngine engine, IDialogService dialogs, bool canChange, ChangeGate gate,
        ActionRunner? actions = null, SystemAction? restartExplorer = null)
    {
        _engine = engine;
        _dialogs = dialogs;
        CanChange = canChange;
        _gate = gate;
        _actions = actions;
        _restartExplorer = restartExplorer;
        gate.PropertyChanged += (_, _) =>
        {
            ApplyCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    /// <summary>Raised after changes were applied (the history should reload).</summary>
    public event EventHandler? ChangesApplied;

    /// <summary>Raised when a scan finished and the findings changed.</summary>
    public event EventHandler? FindingsChanged;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    public ObservableCollection<FindingGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<FindingItemViewModel> AllItems => _all;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasScanned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _selectedCount;

    /// <summary>Sum of SizeBytes of the selected items (only items with a change can be selected).</summary>
    [ObservableProperty]
    private long _selectedBytes;

    [ObservableProperty]
    private string? _lastSummary;

    public bool IsIdle => !IsBusy;
    public bool IsEmpty => HasScanned && _all.Count == 0;
    public int FindingCount => _all.Count;
    public string ApplyText => $"Ausgewählte anwenden ({SelectedCount})";

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken ct)
    {
        IsBusy = true;
        LastSummary = null;
        ProgressText = "Prüfung startet …";
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var findings = await _engine.ScanAsync(progress, ct);
            SetFindings(findings);
            HasScanned = true;
        }
        catch (OperationCanceledException)
        {
            LastSummary = "Prüfung abgebrochen.";
        }
        catch (Exception ex)
        {
            FileLog.Error("Prüfung fehlgeschlagen", ex);
            LastSummary = $"Prüfung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanScan() => !IsBusy;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var selected = _all.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (selected.Count == 0) return;
        var list = selected.Select(i => i.ChangeText ?? i.Title).ToList();
        var question = selected.Count == 1
            ? "Diese Änderung wird angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sie sich rückgängig machen."
            : $"Diese {selected.Count} Änderungen werden angewendet. Vorher legt WinWächter eine Sicherung an; im Verlauf lassen sie sich rückgängig machen.";
        if (!_dialogs.Confirm("Änderungen anwenden", question, "Anwenden", list)) return;

        var appliedShellExt = 0;
        if (!await _gate.RunAsync(this, async () => appliedShellExt = await RunApplyAsync(selected, ct)))
        {
            LastSummary = ChangeGate.BusyHint;
            return;
        }
        // the gate is released now, so the runner can take it for the restart
        if (appliedShellExt > 0) await OfferExplorerRestartAsync();
    }

    async Task OfferExplorerRestartAsync()
    {
        if (_actions is null || _restartExplorer is null) return;
        if (!_dialogs.Ask("Explorer neu starten",
                "Damit die Änderung sofort wirkt, startet WinWächter den Explorer neu. Offene Explorer-Fenster schließen sich.",
                "Neu starten", "Später")) return;
        var outcome = await _actions.RunAsync(this, _restartExplorer, null);
        if (outcome is { Success: false }) _dialogs.Inform("Explorer neu starten", outcome.Message);
    }

    /// <summary>Returns how many "shellext" changes were applied.</summary>
    async Task<int> RunApplyAsync(List<FindingItemViewModel> selected, CancellationToken ct)
    {
        var shellExt = 0;
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync("Optimierung", selected.Select(i => i.Finding).ToList(), progress, ct);

            // the engine applies in order and stops at the first failure or on cancel
            foreach (var done in selected.Take(result.Applied))
            {
                if (done.Finding.Change?.Kind == "shellext") shellExt++;
                RemoveItem(done);
            }
            var notDone = selected.Count - result.Applied - result.Failed;
            var lines = new List<string>
            {
                $"Angewendet: {result.Applied}",
                $"Fehlgeschlagen: {result.Failed}",
            };
            if (notDone > 0) lines.Add($"Nicht ausgeführt: {notDone}");
            lines.Add(result.RestorePoint.Message);

            string message;
            if (result.Failed > 0)
            {
                message = "Eine Änderung ist fehlgeschlagen, danach wurde angehalten. Details stehen im Verlauf.";
                LastSummary = $"Zuletzt: {result.Applied} angewendet, {result.Failed} fehlgeschlagen.";
            }
            else if (notDone > 0)
            {
                message = $"Abgebrochen: {result.Applied} angewendet, {notDone} nicht ausgeführt. Das Angewendete steht im Verlauf.";
                LastSummary = $"Abgebrochen: {result.Applied} angewendet, {notDone} nicht ausgeführt.";
            }
            else
            {
                message = "Die Änderungen sind angewendet und stehen im Verlauf.";
                LastSummary = $"Zuletzt: {result.Applied} angewendet.";
            }
            _dialogs.Inform("Zusammenfassung", message, lines);
            ChangesApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            LastSummary = "Anwenden abgebrochen, bevor etwas geändert wurde.";
        }
        catch (Exception ex)
        {
            FileLog.Error("Anwenden fehlgeschlagen", ex);
            _dialogs.Inform("Anwenden fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
        return shellExt;
    }

    bool CanApply() => CanChange && !IsBusy && !_gate.IsBusy && SelectedCount > 0;

    void SetFindings(IReadOnlyList<Finding> findings)
    {
        _all.Clear();
        Groups.Clear();
        // unknown group names fall back to the area heading
        bool InGroup(Finding f) => f.Group is not null && GroupOrder.Contains(f.Group);
        foreach (var (area, title) in Areas)
            AddGroup(title, findings.Where(f => f.Area == area && !InGroup(f)));
        foreach (var group in GroupOrder)
            AddGroup(group, findings.Where(f => f.Group == group),
                group switch
                {
                    FindingGroups.OrphanFolders => LeftoverFolderScanner.GroupHint,
                    FindingGroups.ContextMenu => ContextMenuHint,
                    _ => null,
                });
        AfterItemsChanged();
    }

    void AddGroup(string title, IEnumerable<Finding> findings, string? hint = null)
    {
        var items = findings
            .OrderByDescending(f => f.Preselected)
            .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new FindingItemViewModel(f, UpdateSelection))
            .ToList();
        if (items.Count == 0) return;
        _all.AddRange(items);
        Groups.Add(new FindingGroupViewModel(title, items, hint));
    }

    void RemoveItem(FindingItemViewModel item)
    {
        _all.Remove(item);
        var group = Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (group is null) return;
        group.Items.Remove(item);
        if (group.Items.Count == 0) Groups.Remove(group);
        AfterItemsChanged();
    }

    void AfterItemsChanged()
    {
        UpdateSelection();
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(IsEmpty));
        FindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdateSelection()
    {
        var selected = _all.Where(i => i.IsSelected && i.CanSelect).ToList();
        SelectedCount = selected.Count;
        SelectedBytes = selected.Sum(i => i.Finding.SizeBytes ?? 0);
    }
}
