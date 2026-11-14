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

/// <summary>The pages added in 0.6 (null = not shown, e.g. in tests that do not need them).</summary>
public sealed record ShellPages(RepairViewModel? Repair = null, DeviceViewModel? Device = null, StorageMapViewModel? Storage = null);

/// <summary>Window-level state: navigation, update banner, admin notice.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(OptimizeViewModel optimize, PerformanceViewModel performance, ProtectViewModel protect,
        SecurityViewModel security, PrivacyViewModel privacy, ProgramsViewModel programs, HistoryViewModel history,
        SettingsViewModel settings, UpdateViewModel update, ImproveViewModel improve, bool isElevated, ShellPages? pages = null)
    {
        Repair = pages?.Repair;
        Device = pages?.Device;
        Storage = pages?.Storage;
        Optimize = optimize;
        Performance = performance;
        Protect = protect;
        Security = security;
        Privacy = privacy;
        Programs = programs;
        History = history;
        Settings = settings;
        Update = update;
        IsElevated = isElevated;
        Overview = new OverviewViewModel(optimize, protect, security, update, improve, Navigate);

        PrimaryNav =
        [
            new NavItem("Übersicht", "\uE80F", Overview, OnSelected),
            new NavItem("Optimieren", "\uE90F", optimize, OnSelected),
            new NavItem("Leistung", "\uEC4A", performance, OnSelected),
            new NavItem("Schutz", "\uEA18", protect, OnSelected),
            new NavItem("Sicherheit", "", security, OnSelected),
            new NavItem("Datenschutz", "", privacy, OnSelected),
            new NavItem("Programme", "\uE71D", programs, OnSelected),
            new NavItem("Verlauf", "\uE81C", history, OnSelected),
        ];
        // 0.6 pages, in the order \u00DCbersicht, Optimieren, Speicherplatz, Leistung, Ger\u00E4t, \u2026, Programme, Reparatur, Verlauf
        if (Storage is { } storage)
            PrimaryNav.Insert(PrimaryNav.IndexOf(PrimaryNav.First(n => ReferenceEquals(n.Page, optimize))) + 1,
                new NavItem("Speicherplatz", "", storage, OnSelected));
        if (Device is { } device)
            PrimaryNav.Insert(PrimaryNav.IndexOf(PrimaryNav.First(n => ReferenceEquals(n.Page, performance))) + 1,
                new NavItem("Ger\u00E4t", "\uE7F8", device, OnSelected));
        if (Repair is { } repair) PrimaryNav.Insert(PrimaryNav.Count - 1, new NavItem("Reparatur", "\uE15E", repair, OnSelected));
        SettingsNav = new NavItem("Einstellungen", "\uE713", settings, OnSelected);
        PrimaryNav[0].IsSelected = true;

        // the history must show what other pages just changed, with the new session selected
        optimize.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        protect.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        security.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        improve.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        // the Defender scan on the Sicherheit page waits while the Schutz page runs a Defender operation
        protect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProtectViewModel.IsDefenderBusy)) security.RunActionCommand.NotifyCanExecuteChanged();
        };
        privacy.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        if (Storage is not null) Storage.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        programs.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
        performance.ChangesApplied += async (_, _) => await history.ShowNewestAsync();
    }

    public RepairViewModel? Repair { get; }
    public DeviceViewModel? Device { get; }
    public StorageMapViewModel? Storage { get; }
    public OverviewViewModel Overview { get; }
    public OptimizeViewModel Optimize { get; }
    public PerformanceViewModel Performance { get; }
    public ProtectViewModel Protect { get; }
    public SecurityViewModel Security { get; }
    public PrivacyViewModel Privacy { get; }
    public ProgramsViewModel Programs { get; }
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
        var previous = CurrentPage;
        CurrentPage = item.Page;
        // the live monitor samples only while its page is the current one
        if (ReferenceEquals(previous, Performance) && !ReferenceEquals(item.Page, Performance)) Performance.OnHidden();
        if (ReferenceEquals(item.Page, Performance) && !ReferenceEquals(previous, Performance))
        {
            Performance.OnShown();
            // the settings are read on the first visit
            if (Performance.Settings.Count == 0 && !Performance.RefreshCommand.IsRunning) Performance.RefreshCommand.Execute(null);
        }
        // the security check normally ran at startup; read it on the first visit otherwise
        if (ReferenceEquals(item.Page, Security) && !Security.IsLoaded && !Security.RefreshCommand.IsRunning)
            Security.RefreshCommand.Execute(null);
        // the privacy page reads the settings the first time it is shown
        if (ReferenceEquals(item.Page, Privacy) && Privacy.Rows.Count == 0 && !Privacy.RefreshCommand.IsRunning)
            Privacy.RefreshCommand.Execute(null);
        // the program list is read on the first visit as well
        if (ReferenceEquals(item.Page, Programs) && !Programs.HasLoaded && !Programs.RefreshCommand.IsRunning)
            Programs.RefreshCommand.Execute(null);
        if (ReferenceEquals(item.Page, Repair) && Repair is { IsLoaded: false } r && !r.LoadCommand.IsRunning)
            r.LoadCommand.Execute(null);
        if (ReferenceEquals(item.Page, Device) && Device is { IsLoaded: false } dv && !dv.RefreshCommand.IsRunning)
            dv.RefreshCommand.Execute(null);
        // and the winget updates (not on every visit: winget takes a few seconds)
        if (ReferenceEquals(item.Page, Programs) && Programs.Updates is { HasLoaded: false } u && !u.RefreshCommand.IsRunning)
            u.RefreshCommand.Execute(null);
    }

    /// <summary>Shows a page: its view model, or its type (the overview leads to the history without holding it).</summary>
    public void Navigate(object page)
    {
        var item = Nav.FirstOrDefault(n => ReferenceEquals(n.Page, page) || (page is Type t && t.IsInstanceOfType(n.Page)));
        if (item is not null) item.IsSelected = true;
    }

    [RelayCommand]
    private void ShowUpdate() => Navigate(Settings);
}
