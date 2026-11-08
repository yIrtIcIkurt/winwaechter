using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinWaechter.App.ViewModels;

/// <summary>One entry of the navigation rail.</summary>
public sealed partial class NavItem(string title, string glyph, object page, Action<NavItem> selected) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;   // Segoe Fluent Icons code point
    public object Page { get; } = page;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>Window-level state: navigation, update banner, admin notice.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(OptimizeViewModel optimize, ProtectViewModel protect, HistoryViewModel history,
        SettingsViewModel settings, UpdateViewModel update, bool isElevated)
    {
        Optimize = optimize;
        Protect = protect;
        History = history;
        Settings = settings;
        Update = update;
        IsElevated = isElevated;
        Overview = new OverviewViewModel(optimize, protect, update, () => Navigate(optimize));

        PrimaryNav =
        [
            new NavItem("Übersicht", "\uE80F", Overview, OnSelected),
            new NavItem("Optimieren", "\uE90F", optimize, OnSelected),
            new NavItem("Schutz", "\uEA18", protect, OnSelected),
            new NavItem("Verlauf", "\uE81C", history, OnSelected),
        ];
        SettingsNav = new NavItem("Einstellungen", "\uE713", settings, OnSelected);
        PrimaryNav[0].IsSelected = true;

        // the history must show what other pages just changed, with the new session selected
        optimize.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        protect.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
    }

    public OverviewViewModel Overview { get; }
    public OptimizeViewModel Optimize { get; }
    public ProtectViewModel Protect { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public UpdateViewModel Update { get; }
    public bool IsElevated { get; }
    public bool ShowLimitedMode => !IsElevated;

    public ObservableCollection<NavItem> PrimaryNav { get; }
    public NavItem SettingsNav { get; }
    IEnumerable<NavItem> Nav => PrimaryNav.Append(SettingsNav);

    [ObservableProperty]
    private object? _currentPage;

    void OnSelected(NavItem item)
    {
        foreach (var n in Nav) if (!ReferenceEquals(n, item)) n.IsSelected = false;
        CurrentPage = item.Page;
    }

    public void Navigate(object page)
    {
        var item = Nav.FirstOrDefault(n => ReferenceEquals(n.Page, page));
        if (item is not null) item.IsSelected = true;
    }

    [RelayCommand]
    private void ShowUpdate() => Navigate(Settings);
}
