using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinWaechter.App.ViewModels;

/// <summary>"Übersicht": state at a glance, the big „Rechner in Ordnung bringen“ and readout tiles that lead into the areas.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    readonly Action<object> _navigate;

    /// <param name="navigate">Shows a page: a page view model, or the type of one the overview does not hold (the history).</param>
    public OverviewViewModel(OptimizeViewModel optimize, ProtectViewModel protect, SecurityViewModel security, UpdateViewModel update,
        ImproveViewModel improve, Action<object> navigate)
    {
        Optimize = optimize;
        Protect = protect;
        Security = security;
        Update = update;
        Improve = improve;
        _navigate = navigate;
        protect.StateChanged += (_, _) => Recompute();
        security.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SecurityText));
            OnPropertyChanged(nameof(SecurityTone));
        };
        improve.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImproveViewModel.OpenCount) or nameof(ImproveViewModel.IsChecking)) Recompute();
        };
        optimize.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OptimizeViewModel.SelectedBytes) or nameof(OptimizeViewModel.HasScanned))
                OnPropertyChanged(nameof(FreeText));
        };
    }

    public OptimizeViewModel Optimize { get; }
    public ProtectViewModel Protect { get; }
    public SecurityViewModel Security { get; }
    public UpdateViewModel Update { get; }
    public ImproveViewModel Improve { get; }

    /// <summary>„Sicherheit“ readout: counts of red and yellow rows.</summary>
    public string SecurityText => Security.TileText;
    public Tone SecurityTone => Security.TileTone;

    /// <summary>The one big sentence: Defender state, then the state of the last check.</summary>
    public string Statement
    {
        get
        {
            var defender = !Protect.IsLoaded ? "Zustand wird gelesen."
                : !Protect.IsAvailable ? "Defender ist nicht aktiv."
                : Protect.ActiveThreatCount == 1 ? "Defender meldet einen aktiven Fund."
                : Protect.ActiveThreatCount > 1 ? $"Defender meldet {Protect.ActiveThreatCount} aktive Funde."
                : Protect.Status!.RealTime ? "Defender ist aktiv." : "Der Echtzeitschutz ist aus.";
            return $"{defender} {SuggestionSentence}";
        }
    }

    string SuggestionSentence => Improve.IsChecking ? "Prüfung läuft."
        : Improve.OpenCount is not { } open ? "Noch nicht geprüft."
        : open == 0 ? "Keine offenen Vorschläge."
        : open == 1 ? "Ein Vorschlag ist offen." : $"{open} Vorschläge sind offen.";

    public Tone StatementTone =>
        !Protect.IsLoaded ? Tone.Neutral
        : !Protect.IsAvailable || Protect.ActiveThreatCount > 0 || Protect.Status?.RealTime != true ? Tone.Alarm
        : OpenSuggestions > 0 || Protect.HeuristicItems.Any(h => h.RatingTone == Tone.Alarm) ? Tone.Watch
        : Tone.Good;

    /// <summary>Recommendations of every area plus „Außerdem empfohlen“ from the last check.</summary>
    public int OpenSuggestions => Improve.OpenCount ?? 0;
    public string OpenSuggestionsText => Improve.OpenCount is { } n ? n.ToString(Labels.De) : "–";

    public const string FreeNote = "Caches werden nach 1 Tag endgültig entfernt, oder sofort über Verlauf → Ablage jetzt leeren.";

    /// <summary>„Wird frei“: size of the selected findings on Optimieren, "–" before a scan there.</summary>
    public string FreeText => Optimize.HasScanned ? Labels.Size(Optimize.SelectedBytes) : "–";

    void Recompute()
    {
        OnPropertyChanged(nameof(Statement));
        OnPropertyChanged(nameof(StatementTone));
        OnPropertyChanged(nameof(OpenSuggestions));
        OnPropertyChanged(nameof(OpenSuggestionsText));
    }

    // the readout tiles lead into their area
    [RelayCommand]
    private void OpenProtect() => _navigate(Protect);

    [RelayCommand]
    private void OpenSecurity() => _navigate(Security);

    [RelayCommand]
    private void OpenOptimize() => _navigate(Optimize);

    [RelayCommand]
    private void OpenHistory() => _navigate(typeof(HistoryViewModel));
}
