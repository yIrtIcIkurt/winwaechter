using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;

namespace WinWaechter.App.ViewModels;

/// <summary>"Einstellungen": version and update, update check on start, Ablage days, folders.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly AppEnvironment _env;
    readonly ISystemShell _shell;

    public SettingsViewModel(AppSettings settings, AppEnvironment env, UpdateViewModel update, ISystemShell shell)
    {
        _settings = settings;
        _env = env;
        _shell = shell;
        Update = update;
        _checkUpdatesOnStart = settings.CheckUpdatesOnStart;
        _ablageDaysText = settings.AblageDays.ToString(Labels.De);
    }

    public UpdateViewModel Update { get; }
    public string VersionText => $"Version {_env.Version}";
    public string DataFolder => _env.DataRoot;
    public string LogFolder => _env.LogDir;
    public string ModeText => _env.IsElevated
        ? "Läuft mit Administratorrechten."
        : "Läuft ohne Administratorrechte (Entwicklungsmodus). Prüfen geht, Änderungen sind gesperrt.";

    [ObservableProperty]
    private bool _checkUpdatesOnStart;

    partial void OnCheckUpdatesOnStartChanged(bool value)
    {
        _settings.CheckUpdatesOnStart = value;
        Save();
    }

    [ObservableProperty]
    private string _ablageDaysText;

    [ObservableProperty]
    private string? _ablageDaysError;

    partial void OnAblageDaysTextChanged(string value)
    {
        if (int.TryParse(value, out var days) && days is >= AppSettings.MinAblageDays and <= AppSettings.MaxAblageDays)
        {
            AblageDaysError = null;
            if (days == _settings.AblageDays) return;
            _settings.AblageDays = days;
            Save();
        }
        else
        {
            AblageDaysError = $"Bitte eine ganze Zahl von {AppSettings.MinAblageDays} bis {AppSettings.MaxAblageDays} eingeben.";
        }
    }

    void Save() => _settings.Save(_env.SettingsPath);

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_env.DataRoot);

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_env.LogDir);
}
