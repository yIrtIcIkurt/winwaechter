using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>A repeating timer the view model can start and stop (App: DispatcherTimer; tests: manual).</summary>
public interface ITickSource
{
    void Start(TimeSpan every, Action tick);
    void Stop();
}

/// <summary>
/// What the Leistung page needs to act (4.3): startup findings from Optimieren, the rules, ending processes behind
/// <see cref="ProcessGuard"/>, the shared action runner and the free space of the system drive. Delay replaces the
/// 5-second wait in tests; HibernationBytes is the size of hiberfil.sys for the low-disk band.
/// </summary>
public sealed record PerformanceTools(
    Func<IReadOnlyList<Finding>> StartupFindings,
    RuleSet Rules,
    IProcessEnder Ender,
    Func<ProcessGuardContext> Guard,
    ActionRunner Actions,
    Func<DriveSpace?> Disk,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    Func<long>? HibernationBytes = null);

/// <summary>One "Bremsten den Start" entry: a matched startup finding offers a button, otherwise a note says why not.</summary>
public sealed class BootDelayViewModel
{
    readonly AsyncRelayCommand _disable;

    internal BootDelayViewModel(string text, string? note, Finding? finding, Func<Finding, Task> disable, Func<bool> mayAct)
    {
        Text = text;
        Note = note;
        Finding = finding;
        _disable = new AsyncRelayCommand(() => disable(Finding!), () => CanDisable && mayAct());
    }

    public string Text { get; }
    public string? Note { get; }
    public Finding? Finding { get; }

    /// <summary>The button is shown; whether it may be clicked now is the command's CanExecute.</summary>
    public bool CanDisable => Finding is not null;

    public IAsyncRelayCommand DisableCommand => _disable;

    internal void NotifyCommands() => _disable.NotifyCanExecuteChanged();
}

/// <summary>One top consumer, already formatted for display, with „Beenden“ and optionally „Nicht mehr mit Windows starten“.</summary>
public sealed partial class ConsumerViewModel : ObservableObject
{
    readonly AsyncRelayCommand _end;
    readonly AsyncRelayCommand _stop;
    readonly bool _canAct;

    internal ConsumerViewModel(string processName, bool canAct, Func<ConsumerViewModel, Task> end,
        Func<Finding, Task> stopAutostart, Func<bool> mayAct)
    {
        ProcessName = processName;
        _canAct = canAct;
        _name = processName;
        _end = new AsyncRelayCommand(() => end(this), () => CanEnd && mayAct());
        _stop = new AsyncRelayCommand(() => stopAutostart(Autostart!), () => CanStopAutostart && mayAct());
    }

    /// <summary>The process name without „.exe“, as the sampler grouped it.</summary>
    public string ProcessName { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _cpuText = PerformanceViewModel.Dash;
    [ObservableProperty] private string _memoryText = PerformanceViewModel.Dash;

    /// <summary>The program's autostart entry with a change, if Optimieren found one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopAutostart))]
    private Finding? _autostart;

    public bool CanEnd => _canAct;
    public bool CanStopAutostart => _canAct && Autostart is not null;

    public IAsyncRelayCommand EndCommand => _end;
    public IAsyncRelayCommand StopAutostartCommand => _stop;

    partial void OnAutostartChanged(Finding? value) => _stop.NotifyCanExecuteChanged();

    internal void NotifyCommands()
    {
        _end.NotifyCanExecuteChanged();
        _stop.NotifyCanExecuteChanged();
    }
}

/// <summary>One row of the Leistung page: current state, a choice, and what differs.</summary>
public sealed partial class PerfSettingViewModel : ObservableObject
{
    readonly Action? _changed;
    readonly Action? _switch;
    readonly IRelayCommand _openLink;
    readonly IRelayCommand _switchToBalanced;

    /// <param name="switchToBalanced">Picks „Ausgewogen“ in the plan row; null when there is no such option.</param>
    public PerfSettingViewModel(PerfSetting setting, bool pageCanChange, ISystemShell shell, Action? changed = null,
        Action? switchToBalanced = null)
    {
        Setting = setting;
        CanChange = setting.Available && pageCanChange;
        _changed = changed;
        _switch = switchToBalanced;
        _chosen = setting.Current; // nothing is preselected
        _openLink = new RelayCommand(() => { if (setting.Link is { } l) shell.OpenLink(l); }, () => setting.Link is not null);
        _switchToBalanced = new RelayCommand(() => _switch?.Invoke(), () => ShowSwitchToBalanced && pageCanChange);
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

    /// <summary>A power mode row locked by another plan offers to pick „Ausgewogen“ in the plan row instead.</summary>
    public bool ShowSwitchToBalanced => _switch is not null && Setting.Id is "power-ac" or "power-dc"
                                        && Setting.UnavailableText == PerformanceScanner.PlanLocked;

    public IRelayCommand SwitchToBalancedCommand => _switchToBalanced;

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
    public const string FastStartupNote = "Der Schnellstart ist an: Gemessen wird nur ein vollständiger Start (Neustart), "
        + "nicht das Hochfahren nach „Herunterfahren“ – siehe „Schnellstart“ unter Einstellungen.";
    public const string StopStartupText = "Nicht mehr mit Windows starten";
    public const string EndConfirm = "Nicht gespeicherte Arbeit geht verloren.";
    public const string EndRefused = "Dieses Programm beendet WinWächter nicht (Windows, Dienst oder geschützt).";
    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);
    static readonly TimeSpan GentleWait = TimeSpan.FromSeconds(5);
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
    readonly Func<bool>? _fastStartup;
    readonly PerformanceTools? _tools;

    Func<PerfSetting, int, IReadOnlyList<IChange>> _changesFor = (_, _) => [];
    // null while not (or not readably) scanned: then no slow-start entry gets a note or a button
    IReadOnlyList<Finding>? _startup;
    volatile bool _visible;
    volatile bool _sampling;
    bool _primeNext;
    DateTime? _lastSample;
    bool _sampleFailureLogged;

    public PerformanceViewModel(
        Func<CancellationToken, Task<(IReadOnlyList<PerfSetting> Settings, Func<PerfSetting, int, IReadOnlyList<IChange>> ChangesFor)>> scan,
        SystemSampler sampler, Func<BootReport> boot, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell,
        bool canChange, ChangeGate gate, Action openOptimize, ITickSource ticks, Func<bool>? fastStartup = null,
        PerformanceTools? tools = null)
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
        _fastStartup = fastStartup;
        _tools = tools;
        gate.PropertyChanged += (_, _) =>
        {
            ApplyCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
            NotifyRowCommands();
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

    /// <summary>Edge/Chrome background switches (group „Im Hintergrund“); empty without an installed browser.</summary>
    public ObservableCollection<PerfSettingViewModel> BrowserBackground { get; } = [];

    /// <summary>One row per Store app that may run in the background.</summary>
    public ObservableCollection<PerfSettingViewModel> BackgroundApps { get; } = [];

    /// <summary>Set when the background list could not be read; shown as a note, never as an app row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundNote))]
    private string? _backgroundNote;

    /// <summary>Names of launchers (Teams, Steam …) with an active autostart; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBackgroundLaunchersHint))]
    private string? _backgroundLaunchersHint;

    public bool ShowBackgroundLaunchersHint => !string.IsNullOrEmpty(BackgroundLaunchersHint);

    static readonly string[] LauncherPrefixes = ["Microsoft Teams", "Teams", "Steam", "EA", "Ubisoft", "Epic Games"];

    void ShowLaunchers()
    {
        var names = _startup?
            .Where(f => f is { Area: FindingArea.Autostart, Change: not null }
                && LauncherPrefixes.Any(p => IsLauncher(f.Title, p)))
            .Select(f => f.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        const string where = "Das regeln Sie unter Optimieren → Autostart.";
        BackgroundLaunchersHint = names.Count switch
        {
            0 => null,
            1 => $"{names[0]} startet mit Windows und läuft im Hintergrund. {where}",
            _ => $"{JoinNames(names)} starten mit Windows und laufen im Hintergrund. {where}",
        };
    }

    // The title starts with the prefix as a whole word („EA app“ yes, „Eagle Browser“ no).
    static bool IsLauncher(string title, string prefix) =>
        title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (title.Length == prefix.Length || !char.IsLetterOrDigit(title[prefix.Length]));

    static string JoinNames(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " und " + names[^1];

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

    public ObservableCollection<BootDelayViewModel> BootSlowest { get; } = [];

    /// <summary>Only with fast startup the boot measurement misses the usual "Herunterfahren" start.</summary>
    [ObservableProperty] private bool _showFastStartupNote;

    // ---- low disk ----

    /// <summary>„Auf Laufwerk C: sind nur noch 8,2 GB frei.“ while the system drive is nearly full, otherwise null.</summary>
    [ObservableProperty] private string? _lowDiskText;

    /// <summary>What frees the most space, one line each (shown under <see cref="LowDiskText"/>).</summary>
    [ObservableProperty] private IReadOnlyList<string> _lowDiskLines = [];

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

    partial void OnIsBusyChanged(bool value) => NotifyRowCommands();

    // ---- monitor ----

    public void OnShown()
    {
        _visible = true;
        // The first sample after a pause only sets a fresh baseline, so the next tick (2 s later) shows
        // a reading over 2 s instead of an average over the hidden time.
        _primeNext = true;
        _ticks.Start(TickEvery, OnTick);
        OnTick();
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
        var prime = _primeNext; // a skipped priming sample is taken by the next tick
        _primeNext = false;
        LastSampling = SampleAsync(prime);
    }

    async Task SampleAsync(bool prime)
    {
        try
        {
            var now = DateTime.UtcNow;
            var since = _lastSample is { } l ? now - l : TickEvery;
            _lastSample = now;
            var sample = await Task.Run(() =>
            {
                if (prime) _sampler.Reset();
                return _sampler.Next(since);
            });
            // priming: CPU and disk would be averages over the pause, so they stay "–"
            if (_visible) Show(prime ? sample with { CpuPercent = null, DiskPercent = null } : sample);
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

        ShowConsumers(s.Top);
    }

    // Rows are kept and moved instead of rebuilt every tick, so a focused button keeps its focus.
    void ShowConsumers(IReadOnlyList<Consumer> top)
    {
        for (var i = 0; i < top.Count; i++)
        {
            var c = top[i];
            var at = -1;
            for (var j = i; j < Top.Count; j++)
            {
                if (string.Equals(Top[j].ProcessName, c.Name, StringComparison.OrdinalIgnoreCase)) { at = j; break; }
            }
            ConsumerViewModel row;
            if (at < 0)
            {
                row = new ConsumerViewModel(c.Name, _tools is not null, EndAsync, DisableStartupAsync, MayAct);
                Top.Insert(i, row);
            }
            else
            {
                row = Top[at];
                if (at != i) Top.Move(at, i);
            }
            row.Name = c.Count > 1 ? $"{c.Name} ({c.Count})" : c.Name;
            row.CpuText = c.CpuPercent is { } p ? $"{p.ToString("F1", De)} %" : Dash;
            row.MemoryText = Labels.Size(c.MemoryBytes);
            row.Autostart = AutostartOf(c.Name);
        }
        while (Top.Count > top.Count) Top.RemoveAt(Top.Count - 1);
    }

    // An autostart entry with a change whose program file is this process („Heavy“ ↔ …\Heavy.exe).
    Finding? AutostartOf(string processName) => _startup?.FirstOrDefault(f =>
        f is { Area: FindingArea.Autostart, Change: not null, IsProtectedVendor: false }
        && !string.IsNullOrWhiteSpace(f.ExePath)
        && string.Equals(Path.GetFileNameWithoutExtension(f.ExePath), processName, StringComparison.OrdinalIgnoreCase));

    static string Percent(double? p) =>
        p is { } v ? $"{Math.Round(v, MidpointRounding.AwayFromZero).ToString("F0", De)} %" : Dash;

    static double Fraction(double? p) => p is { } v ? Math.Clamp(v / 100.0, 0, 1) : 0;

    static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("F1", De);

    /// <summary>„2,4 s“ below 10 s, otherwise whole seconds („42 s“).</summary>
    static string Seconds(TimeSpan t)
    {
        var tenths = Math.Round(t.TotalSeconds, 1, MidpointRounding.AwayFromZero);
        return tenths < 10
            ? $"{tenths.ToString("F1", De)} s"
            : $"{Math.Round(t.TotalSeconds, MidpointRounding.AwayFromZero).ToString("F0", De)} s";
    }

    // ---- refresh ----

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Einstellungen werden gelesen …";
        try
        {
            await ReadSettingsAsync(ct);
            await ReadFastStartupAsync(ct);
            await ReadStartupAsync(ct);
            await ReadBootAsync(ct);
            await ReadDiskAsync(ct);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    async Task ReadSettingsAsync(CancellationToken ct)
    {
        try
        {
            var scan = await Task.Run(() => _scan(ct), ct);
            _changesFor = scan.ChangesFor;
            Settings.Clear();
            BackgroundApps.Clear();
            BrowserBackground.Clear();
            BackgroundNote = null;
            var switchToBalanced = BalancedSwitch(scan.Settings);
            foreach (var s in scan.Settings)
            {
                if (s.Id == PerformanceScanner.BackgroundUnavailableId)
                {
                    BackgroundNote = s.UnavailableText ?? "Nicht lesbar";
                    continue;
                }
                var row = new PerfSettingViewModel(s, CanChange, _shell, UpdatePending, switchToBalanced);
                (s.Group switch { "background" => BackgroundApps, "browser-background" => BrowserBackground, _ => Settings }).Add(row);
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
    }

    // Picks the „Ausgewogen“ option in the plan row; null when the plan row has none (then no button is offered).
    Action? BalancedSwitch(IReadOnlyList<PerfSetting> settings)
    {
        var plan = settings.FirstOrDefault(s => s is { Id: "powerplan", Available: true });
        if (plan?.Options.FirstOrDefault(o => o.PlanId == PowerModeIds.BalancedPlan) is not { } balanced) return null;
        return () =>
        {
            if (Settings.FirstOrDefault(r => r.Setting.Id == "powerplan") is { CanChange: true } row) row.Chosen = balanced.Value;
        };
    }

    async Task ReadFastStartupAsync(CancellationToken ct)
    {
        try
        {
            ShowFastStartupNote = _fastStartup is { } fast && await Task.Run(fast, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Warn("Leistung: Schnellstart-Zustand nicht lesbar", ex);
            ShowFastStartupNote = false;
        }
    }

    // The autostarts, services and tasks from Optimieren: matched to the slow-start entries and the consumers.
    async Task ReadStartupAsync(CancellationToken ct)
    {
        if (_tools is not { } tools) return;
        try
        {
            ProgressText = "Autostarts werden gelesen …";
            _startup = await Task.Run(tools.StartupFindings, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Warn("Leistung: Autostarts nicht lesbar", ex);
            _startup = null;
        }
        foreach (var row in Top) row.Autostart = AutostartOf(row.ProcessName);
        ShowLaunchers();
    }

    async Task ReadBootAsync(CancellationToken ct)
    {
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
    }

    async Task ReadDiskAsync(CancellationToken ct)
    {
        if (_tools is not { } tools) return;
        try
        {
            var (drive, hiberfil) = await Task.Run(() => (tools.Disk(), tools.HibernationBytes?.Invoke() ?? 0), ct);
            ShowDisk(drive, hiberfil);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FileLog.Warn("Leistung: freier Speicherplatz nicht lesbar", ex);
            ShowDisk(null, 0);
        }
    }

    void ShowDisk(DriveSpace? d, long hiberfil)
    {
        if (d is null || !DiskSpace.IsLow(d))
        {
            LowDiskText = null;
            LowDiskLines = [];
            return;
        }
        LowDiskText = $"Auf Laufwerk {d.Drive.TrimEnd('\\', '/')} sind nur noch {Gb(d.FreeBytes)} GB frei.";
        var lines = new List<string> { "Zwischenspeicher und temporäre Dateien (Optimieren)", "Ablage im Verlauf leeren" };
        if (hiberfil > 0) lines.Add($"Ruhezustand-Datei: {Labels.Size(hiberfil)} (Einstellungen → Ruhezustand-Datei)");
        LowDiskLines = lines;
    }

    bool CanRefresh() => !IsBusy;

    void ShowBoot(BootReport r)
    {
        BootText = r.LastBoots.Count > 0 ? Seconds(r.LastBoots[0]) : Dash;
        BootAverageText = r.Average is { } a ? $"Durchschnitt: {Seconds(a)}" : "";
        BootMessage = r.Message;
        BootSlowest.Clear();
        foreach (var d in r.Slowest)
        {
            var match = _tools is { } t && _startup is { } startup ? StartupMatcher.Match(d, startup, t.Rules) : null;
            BootSlowest.Add(new BootDelayViewModel($"{d.Name}: {Seconds(d.Delay)}", match?.Note, match?.Finding,
                DisableStartupAsync, MayAct));
        }
    }

    // ---- apply ----

    IEnumerable<PerfSettingViewModel> AllRows => Settings.Concat(BrowserBackground).Concat(BackgroundApps);

    void UpdatePending() => PendingCount = AllRows.Count(r => r.IsPending);

    /// <summary>Row buttons (disable a start, end a program) need admin rights, a free gate and an idle page.</summary>
    bool MayAct() => CanChange && !IsBusy && !_gate.IsBusy;

    void NotifyRowCommands()
    {
        foreach (var b in BootSlowest) b.NotifyCommands();
        foreach (var c in Top) c.NotifyCommands();
    }

    [RelayCommand]
    private void Reset()
    {
        foreach (var r in AllRows) r.Chosen = r.Setting.Current;
    }

    [RelayCommand]
    private void OpenOptimize() => _openOptimize();

    /// <summary>„Platz schaffen“: Optimieren with a fresh scan (the scan is started by the openOptimize action).</summary>
    [RelayCommand]
    private void MakeSpace() => _openOptimize();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken ct)
    {
        var pending = AllRows.Where(r => r.IsPending).ToList();
        if (pending.Count == 0) return;

        List<IChange> changes;
        var lines = new List<string>();
        try
        {
            changes = [];
            foreach (var r in pending)
            {
                // a browser without any policy so far shows „verwaltet“ afterwards: say so in the confirmation
                var note = r.Setting.Group == "browser-background" && r.Hint is { } h ? " – " + h : "";
                foreach (var c in _changesFor(r.Setting, r.Chosen))
                {
                    changes.Add(c);
                    lines.Add(ChangeText.ConfirmLine(c) + note);
                }
            }
        }
        catch (ArgumentException ex)
        {
            LastSummary = ex.Message;
            return;
        }
        catch (InvalidOperationException ex)
        {
            // e.g. a registry value with an unexpected type: nothing was changed
            FileLog.Warn("Leistung: Änderungen nicht bestimmbar", ex);
            LastSummary = ex.Message;
            _dialogs.Inform("Zusammenfassung", ex.Message);
            return;
        }
        if (changes.Count == 0) return;

        var question = changes.Count == 1
            ? "Diese Änderung wird vorgenommen. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sie sich rückgängig machen."
            : $"Diese {changes.Count} Änderungen werden vorgenommen. Vorher legt WinWächter eine Sicherung an; im Verlauf lässt sich jede Änderung rückgängig machen.";
        if (!_dialogs.Confirm("Leistungs-Einstellungen ändern", question, "Anwenden",
                lines)) return;

        var needsRestart = pending.Any(r => r.NeedsRestart);
        var findings = changes
            .Select(c => new Finding($"perf|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [], Rating.Info, true, c))
            .ToList();
        var ran = await _gate.RunAsync(this, () => RunApplyAsync(findings, needsRestart, ct));
        if (!ran) { LastSummary = ChangeGate.BusyHint; return; }
        await RefreshCommand.ExecuteAsync(null);
    }

    // „Nicht mehr mit Windows starten“ for a slow-start entry or a consumer's autostart: the finding's own change
    // (autostart off, task disabled, service to manual), journalled in a „Leistung“ session.
    async Task DisableStartupAsync(Finding finding)
    {
        if (!MayAct() || finding.Change is not { } change) return;
        if (!_dialogs.Confirm(StopStartupText, SecurityViewModel.ConfirmNote, StopStartupText, [ChangeText.ConfirmLine(change)]))
            return;
        var ran = await _gate.RunAsync(this, () => RunApplyAsync([finding], needsRestart: false, CancellationToken.None));
        if (!ran) { LastSummary = ChangeGate.BusyHint; return; }
        await RefreshCommand.ExecuteAsync(null);
    }

    async Task RunApplyAsync(List<Finding> findings, bool needsRestart, CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = "Sicherung wird vorbereitet …";
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var result = await _engine.ApplyAsync("Leistung", findings, progress, ct);

            var notDone = findings.Count - result.Applied - result.Failed;
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

    // ---- end a program (Aktion, endgültig) ----

    // The guard decides before anything is asked (nothing allowed: only the refusal, no confirmation) and again at
    // run time which of the processes with this name may be ended: never Windows, services, protected vendors or
    // WinWächter itself. The runner confirms („endgültig“), takes the gate and logs.
    async Task EndAsync(ConsumerViewModel row)
    {
        if (_tools is not { } tools || !MayAct()) return;
        var name = row.ProcessName;
        if ((await AllowedAsync(tools, name, CancellationToken.None)).Count == 0)
        {
            LastSummary = EndRefused;
            return;
        }
        var action = new SystemAction("end-process", $"„{name}“ beenden", "Beenden", EndConfirm, true, (_, ct) =>
        {
            IsBusy = true;
            ProgressText = $"„{name}“ wird beendet …";
            return RunEndAsync(tools, name, ct);
        });
        try
        {
            var outcome = await tools.Actions.RunAsync(this, action, null);
            if (outcome is not null) LastSummary = outcome.Message;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // Fails closed: an unreadable process list or guard context allows nothing.
    static async Task<List<RunningProcess>> AllowedAsync(PerformanceTools tools, string name, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                var guard = tools.Guard();
                return tools.Ender.Find(name).Where(p => ProcessGuard.MayEnd(p, guard)).ToList();
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Warn($"Prozesse nicht prüfbar: {name}", ex);
            return [];
        }
    }

    async Task<ActionOutcome> RunEndAsync(PerformanceTools tools, string name, CancellationToken ct)
    {
        // asked again at run time: the program may have started or ended processes meanwhile
        var allowed = await AllowedAsync(tools, name, ct);
        if (allowed.Count == 0) return new ActionOutcome(false, EndRefused);

        // gently first: ask the windows to close, then give the program 5 s
        await Task.Run(() => tools.Ender.CloseWindows(allowed), ct);
        var delay = tools.Delay ?? ((t, c) => Task.Delay(t, c));
        await delay(GentleWait, ct);
        if (!await Task.Run(() => tools.Ender.AnyRunning(allowed), ct)) return new ActionOutcome(true, $"„{name}“ ist beendet.");

        if (!_dialogs.Ask("Erzwingen?", $"„{name}“ reagiert nicht. Jetzt erzwingen? Nicht gespeicherte Arbeit geht verloren.",
                "Erzwingen", "Abbrechen"))
            return new ActionOutcome(false, $"„{name}“ läuft weiter.");
        await Task.Run(() => tools.Ender.Kill(allowed), ct);
        return await Task.Run(() => tools.Ender.AnyRunning(allowed), ct)
            ? new ActionOutcome(false, $"„{name}“ ließ sich nicht beenden.")
            : new ActionOutcome(true, $"„{name}“ ist beendet.");
    }
}
