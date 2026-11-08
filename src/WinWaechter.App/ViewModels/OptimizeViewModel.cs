using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;

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

    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly List<FindingItemViewModel> _all = [];

    public OptimizeViewModel(OptimizerEngine engine, IDialogService dialogs, bool canChange)
    {
        _engine = engine;
        _dialogs = dialogs;
        CanChange = canChange;
    }

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
            ProgressText = "";
            LastSummary = "Prüfung abgebrochen.";
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

        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync("Optimierung", selected.Select(i => i.Finding).ToList(), progress, ct);

            // the engine applies in order and stops at the first failure
            foreach (var done in selected.Take(result.Applied)) RemoveItem(done);
            var notDone = selected.Count - result.Applied - result.Failed;
            var lines = new List<string>
            {
                $"Angewendet: {result.Applied}",
                $"Fehlgeschlagen: {result.Failed}",
            };
            if (notDone > 0) lines.Add($"Nicht ausgeführt: {notDone}");
            lines.Add(result.RestorePoint.Message);
            LastSummary = $"Zuletzt angewendet: {result.Applied}, fehlgeschlagen: {result.Failed}.";
            _dialogs.Inform("Zusammenfassung",
                result.Failed > 0
                    ? "Eine Änderung ist fehlgeschlagen, danach wurde angehalten. Details stehen im Verlauf."
                    : "Die Änderungen sind angewendet und stehen im Verlauf.",
                lines);
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
    }

    bool CanApply() => CanChange && !IsBusy && SelectedCount > 0;

    void SetFindings(IReadOnlyList<Finding> findings)
    {
        _all.Clear();
        Groups.Clear();
        foreach (var (area, title) in Areas)
        {
            var items = findings.Where(f => f.Area == area)
                .OrderByDescending(f => f.Preselected)
                .ThenBy(f => f.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new FindingItemViewModel(f, UpdateSelection))
                .ToList();
            if (items.Count == 0) continue;
            _all.AddRange(items);
            Groups.Add(new FindingGroupViewModel(title, items));
        }
        AfterItemsChanged();
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

    void UpdateSelection() => SelectedCount = _all.Count(i => i.IsSelected);
}
