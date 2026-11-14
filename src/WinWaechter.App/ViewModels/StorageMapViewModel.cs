using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Storage.Map;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>One offer of a folder or file (a button; „Explain“ is a plain sentence).</summary>
public sealed record StorageOfferViewModel(StorageItemViewModel Item, string Text, StorageOfferKind Kind, string? Group, string? Note)
{
    public bool IsButton => Kind != StorageOfferKind.Explain;
    public bool IsText => Kind == StorageOfferKind.Explain;
}

/// <summary>A tile and list row: name, size, share of the current folder and what WinWaechter offers for it.</summary>
public sealed class StorageItemViewModel
{
    internal StorageItemViewModel(string name, string fullPath, long bytes, double share, StorageNode? node, bool isFile, bool isOwnFiles,
        PlaceKind kind, Func<StorageItemViewModel, IReadOnlyList<StorageOfferViewModel>> offers)
    {
        Name = name;
        FullPath = fullPath;
        Bytes = bytes;
        Share = share;
        Node = node;
        IsFile = isFile;
        IsOwnFiles = isOwnFiles;
        Kind = kind;
        Offers = offers(this);
    }

    public string Name { get; }
    public string FullPath { get; }
    public long Bytes { get; }
    public string SizeText => Labels.Size(Bytes);
    public double Share { get; }
    public string ShareText => $"{Share * 100:0} %";
    internal StorageNode? Node { get; }
    public bool IsFile { get; }
    public bool IsOwnFiles { get; }
    internal PlaceKind Kind { get; }
    public bool CanOpen => Node is { Children.Count: > 0 };
    public bool NoAccess => Node?.NoAccess == true;
    public string? NoAccessText => NoAccess ? "Kein Zugriff, nicht mitgezählt" : null;
    public string ToolTip => $"{Name} – {SizeText}";
    public IReadOnlyList<StorageOfferViewModel> Offers { get; }
}

public sealed record StorageCrumb(string Name, StorageNode Node);

public sealed record StorageDriveViewModel(DriveSpace Space)
{
    public string Name => Space.Drive.TrimEnd('\\');
    public string Text => $"{Name} – {Labels.Size(Space.FreeBytes)} frei von {Labels.Size(Space.TotalBytes)}";
    public double UsedShare => Space.TotalBytes > 0 ? 1 - (double)Space.FreeBytes / Space.TotalBytes : 0;
}

/// <summary>
/// „Speicherplatz“: what fills a drive, as tiles and as a sorted list, one level at a time; per entry what
/// WinWaechter can do about it. Nothing is deleted for good here except the recycle bin (final, confirmed).
/// </summary>
public sealed partial class StorageMapViewModel : ObservableObject
{
    public const string OwnFilesName = "Einzelne Dateien in diesem Ordner";

    readonly StorageScanner _scanner;
    readonly Func<PlaceContext> _context;
    readonly OptimizerEngine _engine;
    readonly IFileOps _files;
    readonly IRecycleBin _recycle;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly ChangeGate _gate;
    readonly bool _ownAccount;
    readonly int _ablageDays;
    readonly Action<string> _navigate;
    readonly long _minLargeBytes;
    PlaceContext? _ctx;
    CancellationTokenSource? _cts;

    public StorageMapViewModel(StorageScanner scanner, Func<IReadOnlyList<DriveSpace>> drives, Func<PlaceContext> context, OptimizerEngine engine,
        IFileOps files, IRecycleBin recycle, IDialogService dialogs, ISystemShell shell, ChangeGate gate, bool canChange, bool ownAccount,
        int ablageDays, Action<string> navigate, long minLargeBytes = 100L << 20)
    {
        _scanner = scanner;
        _context = context;
        _engine = engine;
        _files = files;
        _recycle = recycle;
        _dialogs = dialogs;
        _shell = shell;
        _gate = gate;
        CanChange = canChange;
        _ownAccount = ownAccount;
        _ablageDays = ablageDays;
        _navigate = navigate;
        _minLargeBytes = minLargeBytes;
        foreach (var d in drives()) Drives.Add(new StorageDriveViewModel(d));
        _selectedDrive = Drives.FirstOrDefault();
    }

    public event EventHandler? ChangesApplied;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;

    public ObservableCollection<StorageDriveViewModel> Drives { get; } = [];
    public ObservableCollection<StorageItemViewModel> Items { get; } = [];
    public ObservableCollection<StorageItemViewModel> LargestFiles { get; } = [];
    public ObservableCollection<StorageCrumb> Breadcrumbs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private StorageDriveViewModel? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private StorageNode? _current;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelCommand), nameof(OfferCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _resultText;

    public bool HasResult => Current is not null;

    bool CanScan() => !IsScanning && SelectedDrive is not null;

    [RelayCommand(CanExecute = nameof(CanScan))]
    async Task ScanAsync()
    {
        if (SelectedDrive is not { } drive) return;
        _cts = new CancellationTokenSource();
        IsScanning = true;
        StatusText = "Speicherplatz wird untersucht …";
        try
        {
            var progress = new Progress<(long Files, long Bytes)>(p => StatusText = $"{p.Files:N0} Dateien, {Labels.Size(p.Bytes)} …");
            var scan = await _scanner.ScanAsync(drive.Space.Drive, progress, _cts.Token, minLarge: _minLargeBytes);
            _ctx = await Task.Run(_context);
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new StorageCrumb(drive.Name, scan.Root));
            LargestFiles.Clear();
            foreach (var f in scan.LargestFiles)
                LargestFiles.Add(Item(Path.GetFileName(f.FullPath), f.FullPath, f.Bytes, 0, null, isFile: true, isOwnFiles: false));
            Show(scan.Root);
            StatusText = scan.NoAccessCount > 0
                ? $"{scan.Files:N0} Dateien, {Labels.Size(scan.Bytes)}; {scan.NoAccessCount} Ordner ohne Zugriff"
                : $"{scan.Files:N0} Dateien, {Labels.Size(scan.Bytes)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Untersuchung abgebrochen.";
        }
        catch (Exception ex)
        {
            FileLog.Error("Speicherplatz untersuchen fehlgeschlagen", ex);
            StatusText = "Die Untersuchung ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    bool CanCancel() => IsScanning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    void Cancel() => _cts?.Cancel();

    void Show(StorageNode node)
    {
        Current = node;
        Items.Clear();
        var total = Math.Max(node.Bytes, 1);
        foreach (var c in node.Children.OrderByDescending(c => c.Bytes))
            Items.Add(Item(c.Name, c.FullPath, c.Bytes, (double)c.Bytes / total, c, isFile: false, isOwnFiles: false));
        if (node.OwnFileBytes > 0)
            Items.Add(Item(OwnFilesName, node.FullPath, node.OwnFileBytes, (double)node.OwnFileBytes / total, null, isFile: false, isOwnFiles: true));
    }

    StorageItemViewModel Item(string name, string path, long bytes, double share, StorageNode? node, bool isFile, bool isOwnFiles)
    {
        var ctx = _ctx ?? _context();
        var kind = PlaceKinds.Classify(path, ctx);
        var program = kind == PlaceKind.ProgramFolder ? ctx.ProgramFor(path) : null;
        return new StorageItemViewModel(name, path, bytes, share, node, isFile, isOwnFiles, kind, item =>
            (isOwnFiles ? StorageOffers.For(PlaceKind.Normal, null, _ablageDays) : StorageOffers.For(kind, program, _ablageDays))
            .Select(o => new StorageOfferViewModel(item, o.Text, o.Kind, o.Group, o.Note)).ToList());
    }

    [RelayCommand]
    Task OpenAsync(StorageItemViewModel? item)
    {
        if (item?.Node is not { Children.Count: > 0 } node) return Task.CompletedTask;
        Breadcrumbs.Add(new StorageCrumb(node.Name, node));
        Show(node);
        return Task.CompletedTask;
    }

    [RelayCommand]
    void Up()
    {
        if (Breadcrumbs.Count < 2) return;
        Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        Show(Breadcrumbs[^1].Node);
    }

    [RelayCommand]
    void Crumb(StorageCrumb? crumb)
    {
        if (crumb is null) return;
        var i = Breadcrumbs.IndexOf(crumb);
        if (i < 0) return;
        while (Breadcrumbs.Count > i + 1) Breadcrumbs.RemoveAt(Breadcrumbs.Count - 1);
        Show(crumb.Node);
    }

    bool CanOffer(StorageOfferViewModel? o) => o is not null && o.IsButton && !IsScanning && (o.Kind == StorageOfferKind.ShowInExplorer
        || (CanChange && (o.Kind is not (StorageOfferKind.MoveToAblage or StorageOfferKind.EmptyRecycleBin) || _ownAccount)));

    [RelayCommand(CanExecute = nameof(CanOffer))]
    async Task OfferAsync(StorageOfferViewModel? o)
    {
        if (o is null || !CanOffer(o)) return;
        var item = o.Item;
        switch (o.Kind)
        {
            case StorageOfferKind.ShowInExplorer:
                _shell.OpenFolder(item.IsFile ? Path.GetDirectoryName(item.FullPath) ?? item.FullPath : item.FullPath);
                break;
            case StorageOfferKind.Cleanup:
                _navigate("optimize");
                break;
            case StorageOfferKind.OpenHibernation:
                _navigate("performance");
                break;
            case StorageOfferKind.Uninstall:
                _navigate("programs:" + (_ctx?.ProgramFor(item.FullPath)?.DisplayName ?? item.Name));
                break;
            case StorageOfferKind.EmptyRecycleBin:
                await EmptyRecycleBinAsync(item);
                break;
            case StorageOfferKind.MoveToAblage:
                await MoveToAblageAsync(item, o.Note);
                break;
        }
    }

    async Task EmptyRecycleBinAsync(StorageItemViewModel item)
    {
        if (!_dialogs.Confirm("Papierkorb leeren", $"Der Papierkorb wird endgültig geleert ({item.SizeText}).", "Endgültig leeren", final: true))
            return;
        var root = Path.GetPathRoot(item.FullPath) ?? item.FullPath;
        try
        {
            await Task.Run(() => _recycle.Empty(root));
            item.Node?.Detach();
            ResultText = "Der Papierkorb ist leer.";
            if (Current is { } c) Show(c);
        }
        catch (Exception ex)
        {
            FileLog.Error("Papierkorb leeren fehlgeschlagen", ex);
            _dialogs.Inform("Papierkorb leeren", "Der Papierkorb ließ sich nicht leeren: " + ex.Message);
        }
    }

    async Task MoveToAblageAsync(StorageItemViewModel item, string? note)
    {
        if (!_dialogs.Confirm("In die Ablage verschieben",
                $"„{item.Name}“ ({item.SizeText}) kommt in die Ablage. Sie können es im Verlauf zurückholen. {note}".Trim(), "Verschieben"))
            return;
        var change = item.IsFile ? MoveToAblageChange.ForFile(_files, item.FullPath) : MoveToAblageChange.ForFolder(_files, item.FullPath);
        var finding = new Finding("storage|" + item.FullPath, FindingArea.Speicher, item.Name, item.FullPath, [], Rating.Info, true, change,
            SizeBytes: item.Bytes);
        ApplyResult? result = null;
        var ran = await _gate.RunAsync(this, async () =>
            result = await _engine.ApplyAsync("Speicherplatz: in die Ablage", [finding], null, CancellationToken.None));
        if (!ran)
        {
            ResultText = ChangeGate.BusyHint;
            return;
        }
        if (result is { Applied: > 0 })
        {
            ResultText = $"„{item.Name}“ liegt in der Ablage.";
            if (item.Node is { } n) n.Detach();
            if (item.IsFile)
            {
                var large = LargestFiles.FirstOrDefault(f => f.FullPath == item.FullPath);
                if (large is not null) LargestFiles.Remove(large);
            }
            if (Current is { } c) Show(c);
            ChangesApplied?.Invoke(this, EventArgs.Empty);
        }
        else ResultText = $"„{item.Name}“ ließ sich nicht verschieben. Details stehen im Verlauf.";
    }
}
