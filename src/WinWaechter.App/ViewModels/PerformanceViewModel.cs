using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Performance;

namespace WinWaechter.App.ViewModels;

/// <summary>A repeating timer the view model can start and stop (App: DispatcherTimer; tests: manual).</summary>
public interface ITickSource
{
    void Start(TimeSpan every, Action tick);
    void Stop();
}

/// <summary>One top consumer, already formatted for display.</summary>
public sealed record ConsumerViewModel(string Name, string CpuText, string MemoryText);

/// <summary>One row of the Leistung page: current state, a choice, and what differs.</summary>
public sealed partial class PerfSettingViewModel : ObservableObject
{
    readonly Action? _changed;
    readonly IRelayCommand _openLink;

    public PerfSettingViewModel(PerfSetting setting, bool pageCanChange, ISystemShell shell, Action? changed = null)
    {
        Setting = setting;
        CanChange = setting.Available && pageCanChange;
        _changed = changed;
        _chosen = setting.Current; // nothing is preselected
        _openLink = new RelayCommand(() => { if (setting.Link is { } l) shell.OpenLink(l); }, () => setting.Link is not null);
    }

    public PerfSetting Setting { get; }
    public IReadOnlyList<PerfOption> Options => Setting.Options;
    public bool CanChange { get; }
    public string Title => Setting.Title;
    public string Explanation => Setting.Explanation;
    public string? Hint => Setting.Hint;
    public string? Link => Setting.Link;
    public bool NeedsRestart => Setting.NeedsRestart;
    public string? UnavailableText => Setting.UnavailableText;
    public bool IsRecommendedDifferent => Setting.Recommended is { } r && r != Setting.Current;

    /// <summary>Chip text such as „empfohlen: An“ while the recommendation differs from the current state.</summary>
    public string? RecommendedText => IsRecommendedDifferent
        ? $"empfohlen: {Setting.Options.FirstOrDefault(o => o.Value == Setting.Recommended)?.Label}"
        : null;

    public string StateText => Setting.Available
        ? Setting.Options.FirstOrDefault(o => o.Value == Setting.Current)?.Label ?? ""
        : Setting.UnavailableText ?? "";

    public IRelayCommand OpenLinkCommand => _openLink;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private int _chosen;

    public bool IsChanged => Chosen != Setting.Current;

    /// <summary>A pending difference that may actually be applied.</summary>
    public bool IsPending => IsChanged && CanChange;

    partial void OnChosenChanged(int value) => _changed?.Invoke();
}

/// <summary>"Leistung": live readouts, boot time and performance settings that are applied via the engine.</summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    public const string RestartNote = "Einige Änderungen gelten erst nach einem Neustart.";
    public const string Dash = "–";
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    readonly Func<CancellationToken, Task<(IReadOnlyList<PerfSetting> Settings, Func<PerfSetting, int, IReadOnlyList<IChange>> ChangesFor)>> _scan;
    readonly SystemSampler _sampler;
    readonly Func<BootReport> _boot;
    readonly OptimizerEngine _engine;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly ChangeGate _gate;
    readonly Action _openOptimize;
    readonly ITickSource _ticks;

    Func<PerfSetting, int, IReadOnlyList<IChange>> _changesFor = (_, _) => [];
    volatile bool _visible;
    volatile bool _sampling;
    DateTime? _lastSample;
    bool _sampleFailureLogged;

    public PerformanceViewModel(
        Func<CancellationToken, Task<(IReadOnlyList<PerfSetting> Settings, Func<PerfSetting, int, IReadOnlyList<IChange>> ChangesFor)>> scan,
        SystemSampler sampler, Func<BootReport> boot, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell,
        bool canChange, ChangeGate gate, Action openOptimize, ITickSource ticks)
    {
        _scan = scan;
        _sampler = sampler;
        _boot = boot;
        _engine = engine;
        _dialogs = dialogs;
        _shell = shell;
        CanChange = canChange;
        _gate = gate;
        _openOptimize = openOptimize;
        _ticks = ticks;
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
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    /// <summary>All rows except the background apps.</summary>
    public ObservableCollection<PerfSettingViewModel> Settings { get; } = [];

    /// <summary>One row per Store app that may run in the background.</summary>
    public ObservableCollection<PerfSettingViewModel> BackgroundApps { get; } = [];

    /// <summary>Set when the background list could not be read; shown as a note, never as an app row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundNote))]
    private string? _backgroundNote;

    public bool ShowBackgroundNote => !string.IsNullOrEmpty(BackgroundNote);

    /// <summary>Header of the collapsible background list, counting the apps Windows currently allows.</summary>
    public string BackgroundHeader => $"Hintergrund-Apps ({BackgroundApps.Count(r => r.Setting.Current == 1)} erlaubt)";

    // ---- live readouts ----

    [ObservableProperty] private string _cpuText = Dash;
    [ObservableProperty] private string _memoryText = Dash;
    [ObservableProperty] private string _diskText = Dash;
    [ObservableProperty] private double _cpuValue;
    [ObservableProperty] private double _memoryValue;
    [ObservableProperty] private double _diskValue;

    public ObservableCollection<ConsumerViewModel> Top { get; } = [];

    // ---- boot ----

    [ObservableProperty] private string _bootText = Dash;
    [ObservableProperty] private string _bootAverageText = "";
    [ObservableProperty] private string? _bootMessage;

    public ObservableCollection<string> BootSlowest { get; } = [];

    // ---- apply state ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int _pendingCount;

    [ObservableProperty]
    private string? _lastSummary;

    public string ApplyText => $"Änderungen anwenden ({PendingCount})";

    /// <summary>The sampling run started by the latest tick (null before the first one); lets tests await it.</summary>
    public Task? LastSampling { get; private set; }

    // ---- monitor ----

    public void OnShown()
    {
        _visible = true;
        _ticks.Start(TickEvery, OnTick);
    }

    public void OnHidden()
    {
        _visible = false;
        _ticks.Stop();
    }

    void OnTick()
    {
        // SystemSampler is not thread-safe: never let two samples overlap.
        if (!_visible || _sampling) return;
        _sampling = true;
        LastSampling = SampleAsync();
    }

    async Task SampleAsync()
    {
        try
        {
            var now = DateTime.UtcNow;
            var since = _lastSample is { } l ? now - l : TickEvery;
            _lastSample = now;
            var sample = await Task.Run(() => _sampler.Next(since));
            if (_visible) Show(sample);
        }
        catch (Exception ex)
        {
            if (!_sampleFailureLogged)
            {
                _sampleFailureLogged = true;
                FileLog.Warn("Leistung: Messung fehlgeschlagen", ex);
            }
        }
        finally
        {
            _sampling = false;
        }
    }

    void Show(SystemSample s)
    {
        CpuText = Percent(s.CpuPercent);
        CpuValue = Fraction(s.CpuPercent);
        DiskText = Percent(s.DiskPercent);
        DiskValue = Fraction(s.DiskPercent);

        if (s is { MemoryUsedBytes: { } used, MemoryTotalBytes: { } total and > 0 })
        {
            MemoryText = $"{Gb(used)} von {Gb(total)} GB";
            MemoryValue = Math.Clamp((double)used / total, 0, 1);
        }
        else
        {
            MemoryText = Dash;
            MemoryValue = 0;
        }

        Top.Clear();
        foreach (var c in s.Top)
            Top.Add(new ConsumerViewModel(
                c.Count > 1 ? $"{c.Name} ({c.Count})" : c.Name,
                c.CpuPercent is { } p ? $"{p.ToString("F1", De)} %" : Dash,
                Labels.Size(c.MemoryBytes)));
    }

    static string Percent(double? p) =>
        p is { } v ? $"{Math.Round(v, MidpointRounding.AwayFromZero).ToString("F0", De)} %" : Dash;

    static double Fraction(double? p) => p is { } v ? Math.Clamp(v / 100.0, 0, 1) : 0;

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", De);

    static string Seconds(TimeSpan t) => $"{Math.Round(t.TotalSeconds, MidpointRounding.AwayFromZero).ToString("F0", De)} s";

    // ---- refresh ----

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Einstellungen werden gelesen …";
        try
        {
            var scan = await Task.Run(() => _scan(ct), ct);
            _changesFor = scan.ChangesFor;
            Settings.Clear();
            BackgroundApps.Clear();
            BackgroundNote = null;
            foreach (var s in scan.Settings)
            {
                if (s.Id == PerformanceScanner.BackgroundUnavailableId)
                {
                    BackgroundNote = s.UnavailableText ?? "Nicht lesbar";
                    continue;
                }
                var row = new PerfSettingViewModel(s, CanChange, _shell, UpdatePending);
                (s.Group == "background" ? BackgroundApps : Settings).Add(row);
            }
            OnPropertyChanged(nameof(BackgroundHeader));
            UpdatePending();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Error("Leistungs-Einstellungen lesen fehlgeschlagen", ex);
            LastSummary = $"Lesen fehlgeschlagen: {ex.Message}";
        }

        // Called once per refresh: BootReport.Load logs each failing call.
        try
        {
            ShowBoot(await Task.Run(_boot, ct));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Warn("Leistung: Startzeit nicht lesbar", ex);
            ShowBoot(new BootReport([], null, [], BootReport.Unreadable));
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanRefresh() => !IsBusy;

    void ShowBoot(BootReport r)
    {
        BootText = r.LastBoots.Count > 0 ? Seconds(r.LastBoots[0]) : Dash;
        BootAverageText = r.Average is { } a ? $"Durchschnitt: {Seconds(a)}" : "";
        BootMessage = r.Message;
        BootSlowest.Clear();
        foreach (var d in r.Slowest) BootSlowest.Add($"{d.Name}: {Seconds(d.Delay)}");
    }

    // ---- apply ----

    IEnumerable<PerfSettingViewModel> AllRows => Settings.Concat(BackgroundApps);

    void UpdatePending() => PendingCount = AllRows.Count(r => r.IsPending);

    [RelayCommand]
    private void Reset()
    {
        foreach (var r in AllRows) r.Chosen = r.Setting.Current;
    }

    [RelayCommand]
    private void OpenOptimize() => _openOptimize();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var pending = AllRows.Where(r => r.IsPending).ToList();
        if (pending.Count == 0) return;

        List<IChange> changes;
        try
        {
            changes = pending.SelectMany(r => _changesFor(r.Setting, r.Chosen)).ToList();
        }
        catch (ArgumentException ex)
        {
            LastSummary = ex.Message;
            return;
        }
        if (changes.Count == 0) return;

        var question = changes.Count == 1
            ? "Diese Änderung wird vorgenommen. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sie sich rückgängig machen."
            : $"Diese {changes.Count} Änderungen werden vorgenommen. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich jede Änderung rückgängig machen.";
        if (!_dialogs.Confirm("Leistungs-Einstellungen ändern", question, "Anwenden",
                changes.Select(c => c.Description).ToList())) return;

        var needsRestart = pending.Any(r => r.NeedsRestart);
        var ran = await _gate.RunAsync(this, () => RunApplyAsync(changes, needsRestart, ct));
        if (!ran) { LastSummary = ChangeGate.BusyHint; return; }
        await RefreshCommand.ExecuteAsync(null);
    }

    async Task RunApplyAsync(List<IChange> changes, bool needsRestart, CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var findings = changes
                .Select(c => new Finding($"perf|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [], Rating.Info, true, c))
                .ToList();
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync("Leistung", findings, progress, ct);

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
                message = "Die Änderungen sind angewendet und stehen im Verlauf.";
                LastSummary = $"Zuletzt: {result.Applied} angewendet.";
            }
            if (needsRestart && result.Applied > 0) message += " " + RestartNote;
            _dialogs.Inform("Zusammenfassung", message, lines);
            ChangesApplied?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            LastSummary = "Anwenden abgebrochen, bevor etwas geändert wurde.";
        }
        catch (Exception ex)
        {
            FileLog.Error("Leistung anwenden fehlgeschlagen", ex);
            _dialogs.Inform("Anwenden fehlgeschlagen", ex.Message);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanApply() => CanChange && !IsBusy && !_gate.IsBusy && PendingCount > 0;
}
