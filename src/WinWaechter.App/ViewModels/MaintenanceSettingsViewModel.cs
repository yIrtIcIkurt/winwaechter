using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Maintenance;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

public sealed record DayOption(DayOfWeek Day, string Name);

/// <summary>
/// „Automatische Prüfung“ (Einstellungen) and its line plus one-time hint on the overview. Switching it on registers
/// the scheduled task; off removes it. The check only reports, it never changes anything.
/// </summary>
public sealed partial class MaintenanceSettingsViewModel : ObservableObject
{
    static readonly CultureInfo De = new("de-DE");

    readonly AppSettings _settings;
    readonly Action _save;
    readonly ITaskStore _tasks;
    readonly string _exePath;
    readonly string? _userSid;
    readonly Func<CheckResult?> _lastResult;
    readonly IDialogService _dialogs;
    readonly Func<DateTime> _now;
    bool _loading;

    public MaintenanceSettingsViewModel(AppSettings settings, Action save, ITaskStore tasks, string exePath, string? userSid, bool canChange,
        bool ownAccount, Func<CheckResult?> lastResult, IDialogService dialogs, Func<DateTime> now)
    {
        _settings = settings;
        _save = save;
        _tasks = tasks;
        _exePath = exePath;
        _userSid = userSid;
        CanEdit = canChange && ownAccount && !string.IsNullOrEmpty(userSid);
        LockText = !canChange ? AppEnvironment.AdminNotice
            : CanEdit ? null
            : "Die automatische Prüfung lässt sich nur für das eigene Administratorkonto einrichten.";
        _lastResult = lastResult;
        _dialogs = dialogs;
        _now = now;
        Days = Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7)
            .Select(d => new DayOption(d, De.DateTimeFormat.GetDayName(d))).ToList();
        Times = Enumerable.Range(6, 17).Select(h => $"{h:00}:00").ToList();
        _loading = true;
        _enabled = settings.MaintenanceEnabled;
        _selectedDay = Days.Single(d => d.Day == settings.MaintenanceDay);
        _selectedTime = Times.Contains(settings.MaintenanceTime) ? settings.MaintenanceTime : AppSettings.DefaultMaintenanceTime;
        _loading = false;
    }

    public bool CanEdit { get; }
    public string? LockText { get; }
    public IReadOnlyList<DayOption> Days { get; }
    public IReadOnlyList<string> Times { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHint))]
    private bool _enabled;

    [ObservableProperty]
    private DayOption _selectedDay;

    [ObservableProperty]
    private string _selectedTime;

    public bool ShowHint => CanEdit && !Enabled && !_settings.MaintenanceHintDismissed;

    /// <summary>„Letzte automatische Prüfung: … – …“; null before the first check.</summary>
    public string? LastCheckText
    {
        get
        {
            if (_lastResult() is not { } r) return null;
            var what = r.Failed ? "fehlgeschlagen" : ToastContent.Text(r) is { Length: > 0 } t ? t : "alles in Ordnung";
            return $"Letzte automatische Prüfung: {r.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm", De)} – {what}";
        }
    }

    public void RefreshLastCheck() => OnPropertyChanged(nameof(LastCheckText));

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        if (!CanEdit)
        {
            Revert(!value);
            return;
        }
        try
        {
            if (value) Register();
            else MaintenanceTask.Remove(_tasks);
            _settings.MaintenanceEnabled = value;
            _save();
        }
        catch (Exception ex)
        {
            FileLog.Error("Automatische Prüfung: Aufgabe nicht geändert", ex);
            _dialogs.Inform("Automatische Prüfung", "Die geplante Aufgabe ließ sich nicht ändern: " + ex.Message);
            Revert(!value);
        }
    }

    partial void OnSelectedDayChanged(DayOption value) => Rescheduled(() => _settings.MaintenanceDay = value.Day);

    partial void OnSelectedTimeChanged(string value) => Rescheduled(() => _settings.MaintenanceTime = value);

    void Rescheduled(Action apply)
    {
        if (_loading) return;
        apply();
        _save();
        if (!Enabled || !CanEdit) return;
        try { Register(); }
        catch (Exception ex)
        {
            FileLog.Error("Automatische Prüfung: neuer Termin nicht gespeichert", ex);
            _dialogs.Inform("Automatische Prüfung", "Der neue Termin ließ sich nicht eintragen: " + ex.Message);
        }
    }

    void Register() =>
        MaintenanceTask.Register(_tasks, _exePath, _userSid!, SelectedDay.Day, _settings.MaintenanceTimeOfDay, _now());

    void Revert(bool old)
    {
        _loading = true;
        Enabled = old;
        _loading = false;
    }

    [RelayCommand]
    void Enable() => Enabled = true;

    [RelayCommand]
    void DismissHint()
    {
        _settings.MaintenanceHintDismissed = true;
        _save();
        OnPropertyChanged(nameof(ShowHint));
    }
}
