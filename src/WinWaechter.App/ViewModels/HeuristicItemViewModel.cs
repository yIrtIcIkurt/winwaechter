using WinWaechter.Core.Model;

namespace WinWaechter.App.ViewModels;

/// <summary>One finding of the own heuristics with its possible actions.</summary>
public sealed class HeuristicItemViewModel(Finding finding)
{
    public Finding Finding { get; } = finding;
    public string Title => Finding.Title;
    public string Location => Finding.Location;
    public IReadOnlyList<string> Reasons => Finding.Reasons;
    public string RatingText => Labels.Rating(Finding.Rating);
    public Tone RatingTone => Labels.RatingTone(Finding.Rating);
    public string? Publisher => Finding.Publisher;
    public bool IsProcess => Finding.Area is FindingArea.Prozess or FindingArea.Verbindung;
    public bool IsProtectedVendor => Finding.IsProtectedVendor;
    public bool CanKill => IsProcess && !IsProtectedVendor;
    public bool CanDisable => Finding.Change is not null && !IsProtectedVendor;

    public string DisableText => Finding.Change?.Kind switch
    {
        "autostart" => "Autostart deaktivieren",
        "task" => "Aufgabe deaktivieren",
        "service" => "Dienst auf Manuell stellen",
        "file" or "temp" => "In die Ablage verschieben",
        _ => "Deaktivieren",
    };
}

/// <summary>One Defender detection.</summary>
public sealed record ThreatItemViewModel(string Name, string SeverityText, string StatusText, string Resources, bool Active);
