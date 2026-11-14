using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Device;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.ViewModels;

/// <summary>One line of the Gerät page: level dot, title, values, why, fix and extra button.</summary>
public sealed partial class DeviceRowViewModel : ObservableObject
{
    readonly DeviceViewModel _owner;

    internal DeviceRowViewModel(DeviceRow row, DeviceViewModel owner)
    {
        Row = row;
        _owner = owner;
    }

    public DeviceRow Row { get; }
    public string Id => Row.Id;
    public string Title => Row.Title;
    public string Detail => Row.Detail;
    public string Why => Row.Why;

    public Tone Tone => Row.Level switch
    {
        DeviceLevel.Good => Tone.Good,
        DeviceLevel.Attention => Tone.Watch,
        DeviceLevel.Bad => Tone.Alarm,
        _ => Tone.Neutral,
    };

    public string LevelText => Row.Level switch
    {
        DeviceLevel.Good => "Gut",
        DeviceLevel.Attention => "Achtung",
        DeviceLevel.Bad => "Kritisch",
        _ => "Unbekannt",
    };

    public bool HasFix => Row.Fix is not null;
    public string? FixText => Row.Fix?.ButtonText;
    public bool HasExtra => Row.Extra is not null;
    public string? ExtraText => Row.Extra?.Title;

    internal void Refresh()
    {
        FixCommand.NotifyCanExecuteChanged();
        ExtraCommand.NotifyCanExecuteChanged();
    }

    bool CanFix() => Row.Fix is { } f && _owner.CanRun(f);

    [RelayCommand(CanExecute = nameof(CanFix))]
    Task FixAsync() => Row.Fix is { } f ? _owner.RunAsync(f, rescan: true) : Task.CompletedTask;

    bool CanExtra() => Row.Extra is { } e && _owner.CanRun(e);

    [RelayCommand(CanExecute = nameof(CanExtra))]
    Task ExtraAsync() => Row.Extra is { } e ? _owner.RunAsync(e, rescan: false) : Task.CompletedTask;
}

public sealed record DeviceGroupViewModel(string Title, IReadOnlyList<DeviceRowViewModel> Rows);

/// <summary>„Gerät“: battery, disks and drives; a fix per finding where one is possible.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    readonly Func<CancellationToken, Task<IReadOnlyList<DeviceRow>>> _scan;
    readonly ActionRunner _runner;

    public DeviceViewModel(Func<CancellationToken, Task<IReadOnlyList<DeviceRow>>> scan, ActionRunner runner, bool canChange)
    {
        _scan = scan;
        _runner = runner;
        CanChange = canChange;
        if (runner.Lock(ActivityLock.Disk) is { } l) l.PropertyChanged += (_, _) => RefreshRows();
    }

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    public ObservableCollection<DeviceGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _resultText;

    bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    async Task RefreshAsync()
    {
        IsBusy = true;
        ProgressText = "Gerätezustand wird gelesen …";
        try
        {
            var rows = await _scan(CancellationToken.None);
            Groups.Clear();
            foreach (var title in new[] { DeviceScanner.GroupBattery, DeviceScanner.GroupDisks, DeviceScanner.GroupVolumes })
            {
                var inGroup = rows.Where(r => r.Group == title).Select(r => new DeviceRowViewModel(r, this)).ToList();
                if (inGroup.Count > 0) Groups.Add(new DeviceGroupViewModel(title, inGroup));
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Gerätezustand lesen fehlgeschlagen", ex);
            ResultText = "Der Gerätezustand ließ sich nicht lesen: " + ex.Message;
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
            IsLoaded = true;
            RefreshRows();
        }
    }

    internal bool CanRun(SystemAction a) => CanChange && !IsBusy && _runner.Lock(a.LockKey) is not { IsRunning: true };

    internal async Task RunAsync(SystemAction a, bool rescan)
    {
        ActionOutcome? outcome;
        try
        {
            outcome = await _runner.RunAsync(this, a, new Progress<string>(t => ProgressText = t));
        }
        finally
        {
            ProgressText = null;
        }
        if (outcome is null) return;
        ResultText = outcome.Message;
        if (rescan) await RefreshAsync();
    }

    void RefreshRows()
    {
        foreach (var g in Groups)
            foreach (var r in g.Rows) r.Refresh();
    }

    partial void OnIsBusyChanged(bool value) => RefreshRows();
}
