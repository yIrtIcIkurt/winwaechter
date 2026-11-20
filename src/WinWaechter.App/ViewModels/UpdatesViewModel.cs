using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Updates;

namespace WinWaechter.App.ViewModels;

/// <summary>One available update: name, „alt → neu“, selection and its state in the current run.
/// A finished one stays visible (✓ Fertig) until the next search and cannot be selected again.</summary>
public sealed partial class UpdateRowViewModel : ItemRowBase
{
    readonly Action _changed;

    public UpdateRowViewModel(WingetPackage p, Action changed)
    {
        Package = p;
        _changed = changed;
        _isSelected = !p.UnknownVersion;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(State)) return;
            if (IsDone) IsSelected = false;
            OnPropertyChanged(nameof(CanSelect));
        };
    }

    public WingetPackage Package { get; }
    public string Name => Package.Name;
    public string Versions => $"{Package.Installed} → {Package.Available}";
    public bool UnknownVersion => Package.UnknownVersion;
    public string? Note => UnknownVersion ? "Installierte Version unbekannt" : null;
    public bool CanSelect => !IsDone;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

/// <summary>
/// „Updates verfügbar“ on the Programme page: winget updates without the protected ones, installed one by one
/// after a final confirmation (an update cannot be undone in the Verlauf).
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    readonly WingetUpdateService _service;
    readonly ActionRunner _runner;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly Func<Task<bool>> _ensureRestorePoint;
    readonly ActivityLock? _lock;

    public UpdatesViewModel(WingetUpdateService service, ActionRunner runner, IDialogService dialogs, ISystemShell shell, bool canChange,
        Func<Task<bool>> ensureRestorePoint)
    {
        _service = service;
        _runner = runner;
        _dialogs = dialogs;
        _shell = shell;
        CanChange = canChange;
        _ensureRestorePoint = ensureRestorePoint;
        _lock = runner.Lock(ActivityLock.Updates);
        if (_lock is not null) _lock.PropertyChanged += OnLockChanged;
    }

    public bool CanChange { get; }
    public ObservableCollection<UpdateRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Hidden { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMissing), nameof(ShowSourceAgreement), nameof(ShowFailed), nameof(HeaderText))]
    private WingetListStatus _status = WingetListStatus.Ok;

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand), nameof(UpdateOneCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    private int _selectedCount;

    public bool ShowMissing => Status == WingetListStatus.Missing;
    public bool ShowSourceAgreement => Status == WingetListStatus.NeedsSourceAgreement;
    public bool ShowFailed => Status == WingetListStatus.Failed;
    public string HeaderText
    {
        get
        {
            var open = Rows.Count(r => !r.IsDone);
            return open == 0 ? "Alle Programme sind aktuell" : $"Updates verfügbar ({open})";
        }
    }

    /// <summary>After a run that updated something: the list of installed programs reads again (versions changed).</summary>
    public event Action? Installed;
    public string UpdateSelectedText => $"Ausgewählte aktualisieren ({SelectedCount})";
    public string? HiddenText => Hidden.Count == 0 ? null : $"{Hidden.Count} Programme bewusst ausgelassen (geschützt)";
    public string LockedHint => !CanChange ? AppEnvironment.AdminNotice : _lock?.IsRunning == true ? _lock.BusyText : "";

    void OnLockChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        UpdateOneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LockedHint));
    }

    bool CanInstall => CanChange && !IsBusy && _lock?.IsRunning != true;

    [RelayCommand]
    Task RefreshAsync() => LoadAsync(false);

    [RelayCommand]
    async Task AcceptSourceAndSearchAsync()
    {
        if (!_dialogs.Confirm("Bedingungen der winget-Quelle", "Damit nehmen Sie die Bedingungen der winget-Quelle von Microsoft an.",
                "Annehmen und suchen")) return;
        await LoadAsync(true);
    }

    [RelayCommand]
    void OpenStore() => _shell.OpenLink(SystemLinks.AppInstallerStore);

    [RelayCommand]
    void SelectAll()
    {
        foreach (var r in Rows.Where(r => r.CanSelect)) r.IsSelected = true;
    }

    async Task LoadAsync(bool acceptSourceAgreements)
    {
        ProgressText = "Updates werden gesucht …";
        ErrorText = null;
        try
        {
            var (list, offered, hidden) = await _service.LoadAsync(acceptSourceAgreements, CancellationToken.None);
            Rows.Clear();
            foreach (var p in offered) Rows.Add(new UpdateRowViewModel(p, UpdateSelection));
            Hidden.Clear();
            foreach (var p in hidden) Hidden.Add(p.Name);
            Status = list.Status;
            ErrorText = list.Error;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Programm-Updates nicht lesbar", ex);
            Rows.Clear();
            Status = WingetListStatus.Failed;
            ErrorText = "Die Updates ließen sich nicht lesen: " + ex.Message;
        }
        finally
        {
            ProgressText = null;
            HasLoaded = true;
            UpdateSelection();
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(HiddenText));
        }
    }

    void UpdateSelection() => SelectedCount = Rows.Count(r => r.IsSelected);

    bool CanUpdateSelected() => CanInstall && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanUpdateSelected))]
    Task UpdateSelectedAsync() => InstallAsync(Rows.Where(r => r.IsSelected).ToList());

    bool CanUpdateOne(UpdateRowViewModel? row) => CanInstall && row is { CanSelect: true };

    [RelayCommand(CanExecute = nameof(CanUpdateOne))]
    Task UpdateOneAsync(UpdateRowViewModel? row) => row is null ? Task.CompletedTask : InstallAsync([row]);

    async Task InstallAsync(IReadOnlyList<UpdateRowViewModel> rows)
    {
        if (rows.Count == 0 || !CanInstall) return;
        // a package can appear twice (machine and user scope); the run reports the one it installs, by id
        var byId = rows.GroupBy(r => r.Package.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var items = new UiProgress<ItemProgress>(i =>
        {
            if (i.Item is WingetPackage p && byId.TryGetValue(p.Id, out var hit))
                foreach (var row in hit) row.SetState(i.State, i.Text);
        });
        var action = _service.InstallAction(rows.Select(r => r.Package).ToList(), _ensureRestorePoint, _ => Task.CompletedTask, items);
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var ran = false;
        try
        {
            ItemRows.Begin(rows);
            var outcome = await _runner.RunAsync(this, action, new Progress<string>(t => ProgressText = t), _cts.Token);
            if (outcome is null) return;
            ran = true;
            ResultText = outcome.Message;
        }
        finally
        {
            // declined or not started: back to no state; otherwise the lines that never got their turn say so
            if (ran) ItemRows.Finish(rows);
            else foreach (var r in rows) r.SetState(ItemState.None);
            ProgressText = null;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            UpdateSelection();
            OnPropertyChanged(nameof(HeaderText));
            UpdateOneCommand.NotifyCanExecuteChanged();
        }
        // the rows stay with their state until „Neu suchen“; the installed programs below read again
        if (rows.Any(r => r.IsDone)) Installed?.Invoke();
    }

    CancellationTokenSource? _cts;

    bool CanCancel() => IsBusy && _cts is { IsCancellationRequested: false };

    /// <summary>„Abbrechen“: takes effect after the running package; a running setup is never killed.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel()
    {
        _cts?.Cancel();
        ProgressText = "Wird nach dem laufenden Paket abgebrochen …";
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
}
