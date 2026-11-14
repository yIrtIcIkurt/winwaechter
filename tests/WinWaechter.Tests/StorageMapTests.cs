using WinWaechter.Core.Programs;
using WinWaechter.Core.Storage.Map;
using WinWaechter.Core.Sys;

namespace WinWaechter.Tests;

public class StorageMapTests
{
    // a fake file system: folder → entries; a folder in Denied throws like an inaccessible one
    sealed class Tree
    {
        public Dictionary<string, List<StorageEntry>> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Tree Dir(string path, params StorageEntry[] entries)
        {
            Dirs[path] = [.. entries];
            return this;
        }

        public IEnumerable<StorageEntry> Enumerate(string path) =>
            Denied.Contains(path) ? throw new UnauthorizedAccessException(path)
            : Dirs.TryGetValue(path, out var e) ? e : throw new DirectoryNotFoundException(path);
    }

    static StorageEntry F(string dir, string name, long bytes, FileAttributes a = FileAttributes.Normal) =>
        new(name, Path.Combine(dir, name), false, bytes, a);

    static StorageEntry D(string dir, string name, FileAttributes a = FileAttributes.Directory) =>
        new(name, Path.Combine(dir, name), true, 0, a | FileAttributes.Directory);

    const long MB = 1L << 20;

    [Fact]
    public async Task Scanner_skips_reparse_points_counts_placeholders_zero_and_survives_access_denied()
    {
        var t = new Tree()
            .Dir(@"C:\R", D(@"C:\R", "Local"), D(@"C:\R", "OneDrive"), D(@"C:\R", "Geheim"), F(@"C:\R", "a.bin", 10))
            // AppData\Local\Application Data points back up: never followed
            .Dir(@"C:\R\Local", D(@"C:\R\Local", "Application Data", FileAttributes.ReparsePoint), F(@"C:\R\Local", "b.bin", 20))
            .Dir(@"C:\R\Local\Application Data", D(@"C:\R\Local\Application Data", "Local"))
            .Dir(@"C:\R\OneDrive", F(@"C:\R\OneDrive", "film.mp4", 5L << 30, (FileAttributes)0x400000), F(@"C:\R\OneDrive", "alt.zip", 7L << 30, FileAttributes.Offline),
                F(@"C:\R\OneDrive", "lokal.txt", 3));
        t.Denied.Add(@"C:\R\Geheim");
        var scan = await new StorageScanner(t.Enumerate).ScanAsync(@"C:\R", null, CancellationToken.None);
        Assert.Equal(33, scan.Root.Bytes);
        Assert.Equal(1, scan.NoAccessCount);
        Assert.True(scan.Root.Children.Single(c => c.Name == "Geheim").NoAccess);
        var local = scan.Root.Children.Single(c => c.Name == "Local");
        Assert.Equal(0, local.Children.Single(c => c.Name == "Application Data").Bytes);
        Assert.Empty(local.Children.Single(c => c.Name == "Application Data").Children);
        Assert.Equal(3, scan.Root.Children.Single(c => c.Name == "OneDrive").Bytes);
    }

    [Fact]
    public async Task Sizes_roll_up_to_parents()
    {
        var t = new Tree()
            .Dir(@"C:\R", D(@"C:\R", "A"), F(@"C:\R", "x", 1))
            .Dir(@"C:\R\A", D(@"C:\R\A", "B"), F(@"C:\R\A", "y", 10))
            .Dir(@"C:\R\A\B", F(@"C:\R\A\B", "z", 100), F(@"C:\R\A\B", "w", 1000));
        var scan = await new StorageScanner(t.Enumerate).ScanAsync(@"C:\R", null, CancellationToken.None);
        var a = scan.Root.Children.Single();
        Assert.Equal(1111, scan.Root.Bytes);
        Assert.Equal(1, scan.Root.OwnFileBytes);
        Assert.Equal(1110, a.Bytes);
        Assert.Equal(1100, a.Children.Single().Bytes);
        Assert.Equal(4, scan.Root.Files);
        Assert.Same(scan.Root, a.Parent);
        Assert.Equal(4, scan.Files);
    }

    [Fact]
    public async Task Largest_files_respects_min_and_max()
    {
        var entries = Enumerable.Range(1, 30).Select(i => F(@"C:\R", $"f{i}", i * 10 * MB)).ToArray();
        var scan = await new StorageScanner(new Tree().Dir(@"C:\R", entries).Enumerate)
            .ScanAsync(@"C:\R", null, CancellationToken.None, maxLarge: 5, minLarge: 100 * MB);
        Assert.Equal(["f30", "f29", "f28", "f27", "f26"], scan.LargestFiles.Select(f => Path.GetFileName(f.FullPath)));
        var few = await new StorageScanner(new Tree().Dir(@"C:\R", entries).Enumerate)
            .ScanAsync(@"C:\R", null, CancellationToken.None, maxLarge: 100, minLarge: 250 * MB);
        Assert.Equal(6, few.LargestFiles.Count);
    }

    [Fact]
    public async Task Cancel_stops_the_scan()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StorageScanner(new Tree().Dir(@"C:\R", F(@"C:\R", "a", 1)).Enumerate).ScanAsync(@"C:\R", null, cts.Token));
    }

    static PlaceContext Ctx(Func<string, InstalledProgram?>? programFor = null) => new(
        WindowsDir: @"C:\Windows", ProgramFiles: @"C:\Program Files", ProgramFilesX86: @"C:\Program Files (x86)", ProgramData: @"C:\ProgramData",
        UserProfile: @"C:\Users\u", TempDir: @"C:\Users\u\AppData\Local\Temp", OneDriveRoots: [@"C:\Users\u\OneDrive"],
        UserLibraries: [@"C:\Users\u\Downloads", @"C:\Users\u\Desktop", @"C:\Users\u\Documents", @"C:\Users\u\Videos"],
        ProgramFor: programFor ?? (_ => null), IsProtectedPath: p => p.Contains(@"\Lexware\", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(@"C:\Users\u\OneDrive\Downloads\x.zip", PlaceKind.OneDrive)]
    [InlineData(@"C:\Users\u\Downloads\Lexware\x.zip", PlaceKind.Protected)]
    [InlineData(@"C:\Users\u\Downloads\film.mp4", PlaceKind.UserLibrary)]
    [InlineData(@"C:\$Recycle.Bin", PlaceKind.RecycleBin)]
    [InlineData(@"C:\hiberfil.sys", PlaceKind.Hibernation)]
    [InlineData(@"C:\pagefile.sys", PlaceKind.PageFile)]
    [InlineData(@"C:\Windows\WinSxS", PlaceKind.WinSxS)]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download", PlaceKind.WindowsUpdateDownload)]
    [InlineData(@"C:\Users\u\AppData\Local\Temp\abc", PlaceKind.Temp)]
    [InlineData(@"C:\Windows\Temp", PlaceKind.Temp)]
    [InlineData(@"C:\Users\u\AppData\Local\Google\Chrome\User Data\Default\Cache", PlaceKind.BrowserCache)]
    [InlineData(@"C:\Windows\System32", PlaceKind.Windows)]
    [InlineData(@"C:\Program Files\Foo", PlaceKind.ProgramFiles)]
    [InlineData(@"C:\ProgramData\Foo", PlaceKind.ProgramData)]
    [InlineData(@"C:\Users\other\Downloads", PlaceKind.OtherUserProfile)]
    [InlineData(@"D:\Spiele", PlaceKind.Normal)]
    public void Classify_order_protected_beats_user_library_and_onedrive_beats_downloads(string path, PlaceKind expected)
    {
        Assert.Equal(expected, PlaceKinds.Classify(path, Ctx()));
    }

    [Fact]
    public void Program_folder_is_recognised_from_install_location()
    {
        var prog = new InstalledProgram(Hive.LocalMachine, "U", "k", "Spiel", "Studio", "1", null, null, "x", @"C:\Games\Spiel", null, false, ProgramFlag.None);
        var ctx = Ctx(p => p.StartsWith(@"C:\Games\Spiel", StringComparison.OrdinalIgnoreCase) ? prog : null);
        Assert.Equal(PlaceKind.ProgramFolder, PlaceKinds.Classify(@"C:\Games\Spiel", ctx));
    }

    [Fact]
    public void Move_to_ablage_offered_only_in_user_libraries()
    {
        static bool Moves(PlaceKind k) => StorageOffers.For(k, null, 14).Any(o => o.Kind == StorageOfferKind.MoveToAblage);
        Assert.True(Moves(PlaceKind.UserLibrary));
        foreach (var k in Enum.GetValues<PlaceKind>().Where(k => k != PlaceKind.UserLibrary)) Assert.False(Moves(k), k.ToString());
        var move = StorageOffers.For(PlaceKind.UserLibrary, null, 14).Single(o => o.Kind == StorageOfferKind.MoveToAblage);
        Assert.Equal("Der Platz wird frei, wenn die Ablage geleert wird (nach 14 Tagen).", move.Note);
        Assert.All(Enum.GetValues<PlaceKind>(), k => Assert.Contains(StorageOffers.For(k, null, 14), o => o.Kind == StorageOfferKind.ShowInExplorer));
    }

    [Fact]
    public void Uninstall_offer_hidden_for_protected_program()
    {
        var normal = new InstalledProgram(Hive.LocalMachine, "U", "k", "Spiel", "Studio", "1", null, null, "x", @"C:\Games", null, false, ProgramFlag.None);
        Assert.Contains(StorageOffers.For(PlaceKind.ProgramFolder, normal, 14), o => o.Kind == StorageOfferKind.Uninstall);
        Assert.DoesNotContain(StorageOffers.For(PlaceKind.ProgramFolder, normal with { Flag = ProgramFlag.Protected }, 14),
            o => o.Kind == StorageOfferKind.Uninstall);
        Assert.Equal(StorageOfferKind.Cleanup, StorageOffers.For(PlaceKind.Temp, null, 14)[0].Kind);
        Assert.Equal("Von Windows verwaltet, bitte nicht anfassen.", StorageOffers.For(PlaceKind.WinSxS, null, 14)[0].Text);
    }

    [Fact]
    public void Squarify_covers_area_and_keeps_indexes()
    {
        long[] sizes = [6, 6, 4, 3, 2, 2, 1];
        var boxes = Treemap.Squarify(sizes, 600, 400);
        Assert.Equal(7, boxes.Count);
        Assert.Equal(Enumerable.Range(0, 7), boxes.Select(b => b.Index).Order());
        Assert.Equal(600.0 * 400, boxes.Sum(b => b.Box.W * b.Box.H), 6);
        foreach (var (i, box) in boxes)
        {
            Assert.Equal(sizes[i] / 24.0 * 600 * 400, box.W * box.H, 6);
            Assert.InRange(box.X, -1e-9, 600);
            Assert.InRange(box.Y, -1e-9, 400);
            Assert.InRange(box.X + box.W, 0, 600 + 1e-6);
            Assert.InRange(box.Y + box.H, 0, 400 + 1e-6);
        }
        // squarified: no sliver thinner than a tenth of its length for this classic example
        Assert.All(boxes, b => Assert.True(Math.Max(b.Box.W / b.Box.H, b.Box.H / b.Box.W) < 10));
    }

    // Read-only timing on the owner's machine: WW_DEVICE=1. Never runs by default.
    [Fact]
    public async Task Device_scan_of_system_drive_finishes()
    {
        if (Environment.GetEnvironmentVariable("WW_DEVICE") != "1") return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scan = await new StorageScanner().ScanAsync(@"C:\", null, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ww-storage-timing.txt"),
            $"{sw.Elapsed.TotalSeconds:F1} s, {scan.Files} Dateien, {scan.Bytes >> 30} GB, {scan.NoAccessCount} ohne Zugriff");
        Assert.True(scan.Bytes > 0);
    }

    [Fact]
    public void Squarify_ignores_zero_sizes()
    {
        var boxes = Treemap.Squarify([0, 5, -1, 5], 100, 100);
        Assert.Equal([1, 3], boxes.Select(b => b.Index).Order());
        Assert.Empty(Treemap.Squarify([0, 0], 100, 100));
    }
}
