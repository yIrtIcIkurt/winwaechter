using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Rules;

namespace WinWaechter.App.ViewModels;

/// <summary>One privacy setting: checkbox „ausschalten“, title, explanation, state chip.</summary>
public sealed partial class PrivacyRowViewModel : ObservableObject
{
    readonly Action? _selectionChanged;

    public PrivacyRowViewModel(PrivacyRow row, Action? selectionChanged = null)
    {
        Row = row;
        _selectionChanged = selectionChanged;
    }

    public PrivacyRow Row { get; }
    public string Title => Row.Setting.Title;
    public string Explanation => Row.Setting.Explanation;
    public bool IsRecommended => Row.Setting.Recommended;

    public string StateText => Row.State switch
    {
        PrivacyState.On => "An",
        PrivacyState.Partial => "Teilweise",
        PrivacyState.Off => "Aus",
        _ => "Aus (bereits minimal)",
    };

    public Tone StateTone => Row.State is PrivacyState.On or PrivacyState.Partial ? Tone.Watch : Tone.Good;

    public bool CanSelect => Row.Changes.Count > 0;

    // nothing is preselected
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) { IsSelected = false; return; }
        _selectionChanged?.Invoke();
    }
}

/// <summary>One heading with its rows on the Privacy page.</summary>
public sealed record PrivacyGroupViewModel(string Title, IReadOnlyList<PrivacyRowViewModel> Rows);

/// <summary>"Datenschutz": Windows privacy settings with state; selected ones are switched off via the engine.</summary>
public sealed partial class PrivacyViewModel : ObservableObject
{
    public const string RestartNote = "Manche Änderungen wirken erst nach Ab-/Anmelden oder Neustart.";
    public const string ElevationHint = "Ohne Administratorrechte werden manche Einstellungen nicht erkannt (z. B. Recall).";

    readonly Func<CancellationToken, Task<IReadOnlyList<PrivacyRow>>> _scan;
    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly ChangeGate _gate;

    public PrivacyViewModel(Func<CancellationToken, Task<IReadOnlyList<PrivacyRow>>> scan, OptimizerEngine engine,
        IDialogService dialogs, bool canChange, ChangeGate gate)
    {
        _scan = scan;
        _engine = engine;
        _dialogs = dialogs;
        CanChange = canChange;
        _gate = gate;
        gate.PropertyChanged += (_, _) =>
        {
            ApplyCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Raised after changes were applied (the history should reload).</summary>
    public event EventHandler? ChangesApplied;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    /// <summary>Shown while another page holds the change gate.</summary>
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    public ObservableCollection<PrivacyRowViewModel> Rows { get; } = [];

    /// <summary>The same rows by heading (general privacy first, then ads and suggestions); Rows stays the flat list.</summary>
    public ObservableCollection<PrivacyGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _selectedCount;

    [ObservableProperty]
    private string? _lastSummary;

    public string ApplyText => $"Ausgewählte anwenden ({SelectedCount})";

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Einstellungen werden gelesen …";
        try
        {
            var rows = await _scan(ct);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(new PrivacyRowViewModel(r, UpdateSelection));
            RebuildGroups();
            UpdateSelection();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Error("Datenschutz-Einstellungen lesen fehlgeschlagen", ex);
            LastSummary = $"Lesen fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    void RebuildGroups()
    {
        Groups.Clear();
        foreach (var (title, group) in new[] { ("Datenschutz", (string?)null), ("Werbung & Vorschläge", PrivacyGroups.Ads) })
        {
            var rows = Rows.Where(r => r.Row.Setting.Group == group).ToList();
            if (rows.Count > 0) Groups.Add(new PrivacyGroupViewModel(title, rows));
        }
    }

    bool CanRefresh() => !IsBusy;

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var r in Rows.Where(r => r.IsRecommended && r.CanSelect)) r.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var changes = Rows.Where(r => r.IsSelected && r.CanSelect).SelectMany(r => r.Row.Changes).ToList();
        if (changes.Count == 0) return;
        var question = changes.Count == 1
            ? "Dieser Wert wird gesetzt. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich die Änderung rückgängig machen."
            : $"Diese {changes.Count} Werte werden gesetzt. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich jede Änderung rückgängig machen.";
        if (!_dialogs.Confirm("Datenschutz-Einstellungen ändern", question, "Anwenden",
                changes.Select(c => c.Description).ToList())) return;

        var ran = await _gate.RunAsync(this, () => RunApplyAsync(changes, ct));
        if (!ran) { LastSummary = ChangeGate.BusyHint; return; }
        // show the current state again (applied rows now read „Aus“)
        await RefreshCommand.ExecuteAsync(null);
    }

    async Task RunApplyAsync(List<RegValueChange> changes, CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var findings = changes
                .Select(c => new Finding($"privacy|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [], Rating.Info, true, c))
                .ToList();
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync("Datenschutz", findings, progress, ct);

            var notDone = changes.Count - result.Applied - result.Failed;
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
                message = $"Die Änderungen sind angewendet und stehen im Verlauf. {RestartNote}";
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
            FileLog.Error("Datenschutz anwenden fehlgeschlagen", ex);
            _dialogs.Inform("Anwenden fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanApply() => CanChange && !IsBusy && !_gate.IsBusy && SelectedCount > 0;

    void UpdateSelection() => SelectedCount = Rows.Count(r => r.IsSelected && r.CanSelect);
}
