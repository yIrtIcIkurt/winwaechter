using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.App.ViewModels;

/// <summary>"Einstellungen": version and update, rules, update check on start, Ablage days, folders, about.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly AppEnvironment _env;
    readonly ISystemShell _shell;
    readonly Func<CancellationToken, Task<RulesUpdateResult>> _updateRules;

    public const string AuthorText = "Erstellt von Tahsin Yilmaz";

    public SettingsViewModel(AppSettings settings, AppEnvironment env, UpdateViewModel update, ISystemShell shell,
        RulesInfo rules, Func<CancellationToken, Task<RulesUpdateResult>> updateRules)
    {
        _settings = settings;
        _env = env;
        _shell = shell;
        _updateRules = updateRules;
        Update = update;
        RulesText = rules.DisplayText;
        _checkUpdatesOnStart = settings.CheckUpdatesOnStart;
        _ablageDaysText = settings.AblageDays.ToString(Labels.De);
        UpdateRulesCommand = new AsyncRelayCommand(UpdateRulesAsync);
        OpenRepositoryCommand = new RelayCommand(() => _shell.OpenLink(SystemLinks.Repository));
    }

    public UpdateViewModel Update { get; }
    public string VersionText => $"Version {_env.Version}";

    /// <summary>The rules in effect, e.g. „Regeln: Version 2 vom 10.11.2026“.</summary>
    public string RulesText { get; }

    /// <summary>Message of the last rules update.</summary>
    [ObservableProperty]
    private string? _rulesStatus;

    /// <summary>„Regeln aktualisieren“: fetches signed rules from GitHub; they apply from the next start.</summary>
    public IAsyncRelayCommand UpdateRulesCommand { get; }

    /// <summary>„Projekt auf GitHub“.</summary>
    public IRelayCommand OpenRepositoryCommand { get; }

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

    async Task UpdateRulesAsync(CancellationToken ct)
    {
        RulesStatus = "Regeln werden geprüft …";
        try { RulesStatus = (await _updateRules(ct)).Message; }
        catch (OperationCanceledException) { RulesStatus = null; }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_env.DataRoot);

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_env.LogDir);
}
