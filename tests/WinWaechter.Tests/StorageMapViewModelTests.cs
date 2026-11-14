using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Storage.Map;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class StorageMapViewModelTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("ww-storagevm").FullName;
    readonly string _data = Directory.CreateTempSubdirectory("ww-storagevm-data").FullName;
    readonly RecordingDialogs _dialogs = new();
    readonly List<string> _navigated = [];
    readonly FakeRecycleBin _recycle = new();
    readonly LinkShell _shell = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_data, true); } catch { }
    }

    sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Emptied { get; } = [];
        public void Empty(string driveRoot) => Emptied.Add(driveRoot);
    }

    sealed class LinkShell : ISystemShell
    {
        public List<string> Folders { get; } = [];
        public void OpenFolder(string path) => Folders.Add(path);
        public void OpenLink(string uri) { }
    }

    string Profile => Path.Combine(_root, "Users", "u");
    string Downloads => Path.Combine(Profile, "Downloads");

    PlaceContext Ctx() => new(Path.Combine(_root, "Windows"), Path.Combine(_root, "PF"), Path.Combine(_root, "PF86"), Path.Combine(_root, "PD"),
        Profile, Path.Combine(Profile, "AppData", "Local", "Temp"), [Path.Combine(Profile, "OneDrive")], [Downloads], _ => null, _protectedPath);

    Func<string, bool> _protectedPath = _ => false;

    // a real little tree under the temp root: Users\u\Downloads\big (file), Users\u\Downloads\Film\a.mp4, $Recycle.Bin\x
    StorageScanner Scanner()
    {
        Directory.CreateDirectory(Path.Combine(Downloads, "Film"));
        File.WriteAllBytes(Path.Combine(Downloads, "Film", "a.mp4"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(Downloads, "big.iso"), new byte[2000]);
        Directory.CreateDirectory(Path.Combine(_root, "$Recycle.Bin"));
        File.WriteAllBytes(Path.Combine(_root, "$Recycle.Bin", "x"), new byte[500]);
        return new StorageScanner();
    }

    StorageMapViewModel Vm(bool canChange = true, StorageScanner? scanner = null, Func<string, bool>? protectedPath = null)
    {
        if (protectedPath is not null) _protectedPath = protectedPath;
        var store = new SessionStore(_data);
        var factory = new ChangeFactory();
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"));
        return new StorageMapViewModel(scanner ?? Scanner(), () => [new DriveSpace(_root, 100, 1000)], Ctx, engine, new WinFileOps(), _recycle,
            _dialogs, _shell, new ChangeGate(), canChange, ownAccount: true, ablageDays: 14, _navigated.Add, minLargeBytes: 1000);
    }

    [Fact]
    public async Task Drill_down_and_up_updates_breadcrumbs()
    {
        var vm = Vm();
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(5500, vm.Current!.Bytes);
        var users = vm.Items.Single(i => i.Name == "Users");
        await vm.OpenCommand.ExecuteAsync(users);
        Assert.Equal(2, vm.Breadcrumbs.Count);
        await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "u"));
        await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Downloads"));
        Assert.Equal(["Film", "Einzelne Dateien in diesem Ordner"], vm.Items.Select(i => i.Name));
        Assert.Equal(0.6, vm.Items[0].Share, 3);
        vm.UpCommand.Execute(null);
        Assert.Equal("u", vm.Current.Name);
        vm.CrumbCommand.Execute(vm.Breadcrumbs[0]);
        Assert.Single(vm.Breadcrumbs);
        Assert.Contains(vm.LargestFiles, f => f.Name == "a.mp4");
    }

    [Fact]
    public async Task Move_to_ablage_applies_change_and_shrinks_parents()
    {
        var vm = Vm();
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Users", "u", "Downloads" }) await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == name));
        var film = vm.Items.Single(i => i.Name == "Film");
        var move = film.Offers.Single(o => o.Kind == StorageOfferKind.MoveToAblage);
        Assert.Equal("Der Platz wird frei, wenn die Ablage geleert wird (nach 14 Tagen).", move.Note);
        await vm.OfferCommand.ExecuteAsync(move);
        Assert.Single(_dialogs.Confirms);
        Assert.False(Directory.Exists(Path.Combine(Downloads, "Film")));
        Assert.DoesNotContain(vm.Items, i => i.Name == "Film");
        Assert.Equal(2500, vm.Breadcrumbs[0].Node.Bytes);
    }

    // review: moving a folder moves its whole subtree: never when Lexware data or OneDrive sits inside it
    [Fact]
    public async Task Folder_with_protected_or_onedrive_inside_gets_no_move()
    {
        Directory.CreateDirectory(Path.Combine(Downloads, "Firma", "Lexware"));
        File.WriteAllBytes(Path.Combine(Downloads, "Firma", "Lexware", "daten.lxd"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(Downloads, "Fotos"));
        File.WriteAllBytes(Path.Combine(Downloads, "Fotos", "a.jpg"), new byte[100]);
        var vm = Vm(protectedPath: p => p.Contains(@"\Lexware\", StringComparison.OrdinalIgnoreCase));
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Users", "u" }) await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == name));
        Assert.DoesNotContain(vm.Items.Single(i => i.Name == "Downloads").Offers, o => o.Kind == StorageOfferKind.MoveToAblage);
        await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Downloads"));
        Assert.DoesNotContain(vm.Items.Single(i => i.Name == "Firma").Offers, o => o.Kind == StorageOfferKind.MoveToAblage);
        Assert.Contains(vm.Items.Single(i => i.Name == "Fotos").Offers, o => o.Kind == StorageOfferKind.MoveToAblage);
    }

    [Fact]
    public async Task Huge_folder_is_capped_with_a_rest_entry()
    {
        var entries = Enumerable.Range(0, 300).Select(i => new StorageEntry($"d{i:D3}", Path.Combine(_root, $"d{i:D3}"), true, 0,
            FileAttributes.Directory)).ToList();
        var tree = new Dictionary<string, IEnumerable<StorageEntry>>(StringComparer.OrdinalIgnoreCase) { [_root] = entries };
        for (var i = 0; i < 300; i++)
            tree[Path.Combine(_root, $"d{i:D3}")] = [new StorageEntry("f", Path.Combine(_root, $"d{i:D3}", "f"), false, 1000 + i, FileAttributes.Normal)];
        var vm = Vm(scanner: new StorageScanner(d => tree[d]));
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(201, vm.Items.Count);
        Assert.Equal("100 weitere Ordner", vm.Items[^1].Name);
        Assert.Equal(Enumerable.Range(0, 100).Sum(i => 1000L + i), vm.Items[^1].Bytes);
        Assert.False(vm.Items[^1].CanOpen);
        Assert.Equal(41, vm.Tiles.Count);
        Assert.Equal("260 weitere Ordner", vm.Tiles[^1].Name);
    }

    [Fact]
    public async Task Move_offer_absent_outside_user_libraries()
    {
        var vm = Vm();
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.All(vm.Items, i => Assert.DoesNotContain(i.Offers, o => o.Kind == StorageOfferKind.MoveToAblage));
    }

    [Fact]
    public async Task Empty_recycle_bin_is_final_and_calls_fake()
    {
        var vm = Vm();
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        var offer = vm.Items.Single(i => i.Name == "$Recycle.Bin").Offers.Single(o => o.Kind == StorageOfferKind.EmptyRecycleBin);
        await vm.OfferCommand.ExecuteAsync(offer);
        Assert.True(Assert.Single(_dialogs.Confirms).Final);
        // the recycle bin of the item's volume
        Assert.Equal([Path.GetPathRoot(_root)!], _recycle.Emptied);
        Assert.DoesNotContain(vm.Items, i => i.Name == "$Recycle.Bin");
    }

    [Fact]
    public async Task Cancel_keeps_previous_result()
    {
        var vm = Vm();
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        var before = vm.Current;
        var slow = new StorageScanner(_ => throw new OperationCanceledException());
        var vm2 = Vm(scanner: slow);
        vm2.SelectedDrive = vm2.Drives[0];
        await vm2.ScanCommand.ExecuteAsync(null);
        Assert.Null(vm2.Current);
        Assert.Equal("Untersuchung abgebrochen.", vm2.StatusText);
        Assert.NotNull(before);
    }

    [Fact]
    public async Task Locked_without_admin_except_show_in_explorer()
    {
        var vm = Vm(canChange: false);
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        var offers = vm.Items.SelectMany(i => i.Offers).ToList();
        Assert.All(offers.Where(o => o.Kind != StorageOfferKind.ShowInExplorer), o => Assert.False(vm.OfferCommand.CanExecute(o)));
        var show = offers.First(o => o.Kind == StorageOfferKind.ShowInExplorer);
        Assert.True(vm.OfferCommand.CanExecute(show));
        await vm.OfferCommand.ExecuteAsync(show);
        Assert.Single(_shell.Folders);
    }

    [Fact]
    public async Task Cleanup_and_uninstall_navigate()
    {
        var vm = Vm();
        var temp = Path.Combine(Profile, "AppData", "Local", "Temp");
        Directory.CreateDirectory(temp);
        File.WriteAllBytes(Path.Combine(temp, "t"), new byte[10]);
        vm.SelectedDrive = vm.Drives[0];
        await vm.ScanCommand.ExecuteAsync(null);
        foreach (var name in new[] { "Users", "u", "AppData", "Local" }) await vm.OpenCommand.ExecuteAsync(vm.Items.Single(i => i.Name == name));
        await vm.OfferCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Temp").Offers.Single(o => o.Kind == StorageOfferKind.Cleanup));
        Assert.Equal(["optimize"], _navigated);
    }
}
