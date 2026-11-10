using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinWaechter.App.ViewModels;

/// <summary>"Übersicht": state at a glance and the big "Prüfen" button.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    readonly Action _showResults;

    public OverviewViewModel(OptimizeViewModel optimize, ProtectViewModel protect, SecurityViewModel security, UpdateViewModel update, Action showResults)
    {
        Optimize = optimize;
        Protect = protect;
        Security = security;
        Update = update;
        _showResults = showResults;
        optimize.FindingsChanged += (_, _) => Recompute();
        protect.StateChanged += (_, _) => Recompute();
        security.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SecurityText));
            OnPropertyChanged(nameof(SecurityTone));
        };
        optimize.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OptimizeViewModel.IsBusy)) { Recompute(); CheckCommand.NotifyCanExecuteChanged(); }
            else if (e.PropertyName is nameof(OptimizeViewModel.SelectedBytes) or nameof(OptimizeViewModel.HasScanned))
                OnPropertyChanged(nameof(FreeText));
        };
    }

    public OptimizeViewModel Optimize { get; }
    public ProtectViewModel Protect { get; }
    public SecurityViewModel Security { get; }
    public UpdateViewModel Update { get; }

    /// <summary>„Sicherheit“ readout: counts of red and yellow rows.</summary>
    public string SecurityText => Security.TileText;
    public Tone SecurityTone => Security.TileTone;

    /// <summary>The one big sentence: Defender state, then the optimizer state.</summary>
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

    string SuggestionSentence => Optimize.IsBusy ? "Prüfung läuft."
        : !Optimize.HasScanned ? "Noch nicht geprüft."
        : OpenSuggestions == 0 ? "Keine offenen Vorschläge."
        : OpenSuggestions == 1 ? "Ein Vorschlag ist offen." : $"{OpenSuggestions} Vorschläge sind offen.";

    public Tone StatementTone =>
        !Protect.IsLoaded ? Tone.Neutral
        : !Protect.IsAvailable || Protect.ActiveThreatCount > 0 || Protect.Status?.RealTime != true ? Tone.Alarm
        : OpenSuggestions > 0 || Protect.HeuristicItems.Any(h => h.RatingTone == Tone.Alarm) ? Tone.Watch
        : Tone.Good;

    public int OpenSuggestions => Optimize.FindingCount + Protect.HeuristicCount;
    public string OpenSuggestionsText => Optimize.HasScanned || Protect.HeuristicScanned ? OpenSuggestions.ToString(Labels.De) : "–";

    public const string FreeNote = "Caches werden nach 1 Tag endgültig entfernt, oder sofort über Verlauf → Ablage jetzt leeren.";

    /// <summary>„Wird frei“: size of the selected findings, "–" before a scan.</summary>
    public string FreeText => Optimize.HasScanned ? Labels.Size(Optimize.SelectedBytes) : "–";

    void Recompute()
    {
        OnPropertyChanged(nameof(Statement));
        OnPropertyChanged(nameof(StatementTone));
        OnPropertyChanged(nameof(OpenSuggestions));
        OnPropertyChanged(nameof(OpenSuggestionsText));
        OnPropertyChanged(nameof(FreeText));
    }

    bool CanCheck() => !Optimize.IsBusy;

    /// <summary>Runs the optimizer scan and the heuristics together and shows the results.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        _showResults();
        var scan = Optimize.ScanCommand.CanExecute(null) ? Optimize.ScanCommand.ExecuteAsync(null) : Task.CompletedTask;
        var heur = Protect.HeuristicScanCommand.IsRunning ? Task.CompletedTask : Protect.HeuristicScanCommand.ExecuteAsync(null);
        await Task.WhenAll(scan, heur);
    }
}
