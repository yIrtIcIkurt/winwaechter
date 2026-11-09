using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinWaechter.Core.Model;

namespace WinWaechter.App.ViewModels;

/// <summary>One finding row: checkbox, title, location, rating chip and reasons.</summary>
public sealed partial class FindingItemViewModel : ObservableObject
{
    readonly Action? _selectionChanged;

    public FindingItemViewModel(Finding finding, Action? selectionChanged = null)
    {
        Finding = finding;
        _selectionChanged = selectionChanged;
        // Only items the rules know as expendable come preselected; unknown ones never.
        _isSelected = finding.Preselected && finding.Change is not null;
    }

    public Finding Finding { get; }
    public string Title => Finding.Title;
    public string Location => Finding.Location;
    public IReadOnlyList<string> Reasons => Finding.Reasons;
    public string ReasonSummary => Finding.Reasons.Count == 1 ? "1 Grund" : $"{Finding.Reasons.Count} Gründe";
    public string RatingText => Labels.Rating(Finding.Rating);
    public Tone RatingTone => Labels.RatingTone(Finding.Rating);
    public string? Publisher => Finding.Publisher;
    public string? SizeText => Finding.SizeBytes is { } b ? Labels.Size(b) : null;
    public bool CanSelect => Finding.Change is not null;
    public string? ChangeText => Finding.Change?.Description;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) { IsSelected = false; return; }
        _selectionChanged?.Invoke();
    }
}

/// <summary>Findings of one heading: an area (Autostart, Dienste, Aufgaben, Speicher) or a FindingGroups group.</summary>
public sealed class FindingGroupViewModel : ObservableObject
{
    public FindingGroupViewModel(string title, IEnumerable<FindingItemViewModel> items, string? hint = null)
    {
        Title = title;
        Hint = hint;
        Items = new(items);
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SizeText));
    }

    public string Title { get; }

    /// <summary>Short note under the title (e.g. for „Verwaiste Ordner“), null for none.</summary>
    public string? Hint { get; }

    public ObservableCollection<FindingItemViewModel> Items { get; }

    /// <summary>Sum of the item sizes, null when no item has a size.</summary>
    public string? SizeText
    {
        get
        {
            var sizes = Items.Select(i => i.Finding.SizeBytes).OfType<long>().ToList();
            return sizes.Count == 0 ? null : Labels.Size(sizes.Sum());
        }
    }
}
