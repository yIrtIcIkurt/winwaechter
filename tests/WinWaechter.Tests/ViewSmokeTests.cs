using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.App.Views;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

/// <summary>Loads the App resources and lays out every page in light and dark, so XAML errors fail a test.</summary>
public sealed class ViewSmokeTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-view-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Dialogs : IDialogService
    {
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) => false;
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    sealed class Shell : ISystemShell
    {
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) { }
    }

    sealed class YesDialogs : IDialogService
    {
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) => true;
        public bool Ask(string title, string message, string yesText, string noText) => true;
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    sealed class Updates : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) =>
            Task.FromResult(new UpdateCheckResult(new UpdateInfo(new SemVer(0, 2, 0), "Neu: alles.",
                new Uri("https://github.com/a"), new Uri("https://github.com/b"), new Uri("https://github.com/c"), "WinWaechter-Setup-0.2.0.exe"),
                "Version 0.2.0 ist verfügbar."));

        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Launch(VerifiedUpdate update) { }
    }

    sealed class NoChange(string target) : IChange
    {
        public string Kind => "autostart";
        public string Target => target;
        public string Description => "Autostart „" + target + "“ ausschalten";
        public ChangeRecord Apply(SessionContext ctx) => throw new NotSupportedException();
        public void Undo(ChangeRecord record, SessionContext ctx) { }
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    [Fact]
    public void All_pages_render_in_light_and_dark()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { RenderAll(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    void RenderAll()
    {
        var app = new App.App();
        app.InitializeComponent();
        Sidebar_icon_resource_exists();

        var findings = new List<Finding>
        {
            new("a", FindingArea.Autostart, "Steam", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Steam",
                ["Laut Regelliste entbehrlich", "Startet bei jeder Anmeldung"], Rating.Entbehrlich, true, new NoChange("Steam")),
            new("b", FindingArea.Aufgabe, "Updater", @"\Vendor\Updater", ["versteckt"], Rating.Info, false, new NoChange("Updater")),
            new("c", FindingArea.Speicher, "Temp", @"C:\Users\x\AppData\Local\Temp", ["älter als 2 Tage"], Rating.Entbehrlich, true,
                new NoChange("Temp"), SizeBytes: 123_456_789),
            new("d", FindingArea.Speicher, "OldTool", @"C:\Program Files\OldTool", ["Kein installiertes Programm gefunden"], Rating.Info, false,
                new NoChange("OldTool"), SizeBytes: 45_678, Group: FindingGroups.OrphanFolders),
        };
        var store = new SessionStore(_root);
        var j = store.Create("Optimierung");
        j.Upsert(new ChangeRecord("1", "autostart", "Steam", "Autostart „Steam“ ausschalten", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now));
        j.Upsert(new ChangeRecord("2", "temp", "Temp", "Temp-Dateien in die Ablage", new(), new() { ["purged"] = "true" }, ChangeStatus.Applied, DateTimeOffset.Now));
        j.Upsert(new ChangeRecord("3", "task", "T", "Aufgabe deaktivieren", new(), new(), ChangeStatus.Failed, DateTimeOffset.Now, "Zugriff verweigert"));
        // 4.4: an app only the Store can bring back (button „Im Store öffnen“, no „Rückgängig“)
        j.Upsert(new ChangeRecord("4", "appx", "Microsoft.Copilot_8wekyb3d8bbwe", "„Copilot“ entfernen",
            new() { ["family"] = "Microsoft.Copilot_8wekyb3d8bbwe" }, new() { ["restorable"] = "false" }, ChangeStatus.Applied, DateTimeOffset.Now));

        var engine = new OptimizerEngine([() => findings], store, new ChangeFactory(), _ => new RestorePointResult(true, "ok"));
        var dialogs = new Dialogs();
        var gate = new ChangeGate();
        var optimize = new OptimizeViewModel(engine, dialogs, canChange: false, gate);
        var heur = new List<Finding>
        {
            new("h", FindingArea.Prozess, "miner.exe", @"C:\Users\x\AppData\Roaming\miner.exe",
                ["Programmname entspricht einem bekannten Kryptominer"], Rating.Verdaechtig, true, new NoChange("miner")),
        };
        var protect = new ProtectViewModel(new FakeDefender(), (_, _) => Task.FromResult<IReadOnlyList<Finding>>(heur),
            engine, _ => 0, dialogs, canChange: false, gate);
        var reg = new FakeRegistry();
        var privacy = new PrivacyViewModel(_ => Task.FromResult(new PrivacyScanner(reg, RuleSet.Default).Scan(_ => true)),
            engine, dialogs, canChange: false, gate);
        var history = new HistoryViewModel(store, engine, new AblageJanitor(store), dialogs, canChange: false, gate, new Shell());
        var env = new AppEnvironment(false, _root, new SemVer(0, 1, 0));
        var update = new UpdateViewModel(new Updates(), dialogs, canInstall: false, () => { }, gate);
        var settings = new SettingsViewModel(new AppSettings(), env, update, new Shell(),
            new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")));
        var programs = Programs(engine, gate);
        var security = Security(engine, gate);
        var perfTicks = new FakeTicks();
        var perfSources = new FakeSystemSources
        {
            Cores = 4,
            Procs = [new(10, "chrome", TimeSpan.Zero, 200L << 20), new(11, "chrome", TimeSpan.Zero, 100L << 20),
                new(12, "Code", TimeSpan.Zero, 400L << 20)],
        };
        var perfReg = new FakeRegistry();
        perfReg.SeedKey(Hive.CurrentUser, WinWaechter.Core.Performance.PerfValues.BackgroundRoot + @"\Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        perfReg.SeedKey(Hive.CurrentUser, WinWaechter.Core.Performance.PerfValues.BackgroundRoot + @"\Microsoft.YourPhone_8wekyb3d8bbwe");
        perfReg.Seed(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 1,
            Microsoft.Win32.RegistryValueKind.DWord);
        var perfNames = new FakeAppDisplayNames();
        perfNames.Names["Microsoft.WindowsCalculator_8wekyb3d8bbwe"] = "Rechner";
        perfNames.Names["Microsoft.YourPhone_8wekyb3d8bbwe"] = "Smartphone-Link";
        // 4.3: a slow start with a matching autostart (button), one without (note), a low system drive, an ender
        var perfBoot = new FakeBootEventSource();
        perfBoot.Events.Add(new(100, DateTime.UtcNow.AddHours(-1), null, 41_000, null));
        perfBoot.Events.Add(new(101, DateTime.UtcNow.AddHours(-1), "Visual Studio Code", null, 6_400, "Code.exe"));
        perfBoot.Events.Add(new(102, DateTime.UtcNow.AddHours(-1), "Treiber.sys", null, 3_100));
        Finding codeAutostart = new("s", FindingArea.Autostart, "Code", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Code", [],
            Rating.Info, false, new NoChange("Code"), ExePath: @"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe");
        var perfTools = new PerformanceTools(() => [codeAutostart], RuleSet.Default, new FakeProcessEnder(),
            () => new ProcessGuardContext(@"C:\Windows", 1, null, new HashSet<string>(), _ => false), new ActionRunner(dialogs, gate),
            () => new DriveSpace(@"C:\", 8L << 30, 500L << 30), (_, _) => Task.CompletedTask, () => 13L << 30);
        var performance = FakePerformanceVm.Create(engine, dialogs, new Shell(), gate, perfTicks, canChange: true, sources: perfSources,
            registry: perfReg, names: perfNames, tools: perfTools, boot: perfBoot);
        var shell = new ShellViewModel(optimize, performance, protect, security, privacy, programs, history, settings, update, isElevated: false);

        // fill the view models (no sync context here, so the commands finish inline or on the pool)
        optimize.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.HeuristicScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        history.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        privacy.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        security.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        privacy.SelectRecommendedCommand.Execute(null);
        update.CheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        performance.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        // two samples: showing the page primes the baseline, the next tick has CPU differences for the consumer rows
        performance.OnShown();
        performance.LastSampling?.GetAwaiter().GetResult();
        perfSources.CpuValue = new(100, 50, 50);
        perfSources.Procs = [new(10, "chrome", TimeSpan.FromMilliseconds(500), 200L << 20),
            new(11, "chrome", TimeSpan.FromMilliseconds(100), 100L << 20), new(12, "Code", TimeSpan.FromMilliseconds(200), 400L << 20)];
        perfTicks.Fire();
        performance.LastSampling?.GetAwaiter().GetResult();
        performance.OnHidden();
        performance.Settings.First(r => r.CanChange).Chosen = performance.Settings.First(r => r.CanChange).Options
            .First(o => o.Value != performance.Settings.First(r => r.CanChange).Setting.Current).Value;
        Assert.True(performance.ShowFastStartupNote);
        Assert.True(performance.BootSlowest.Count == 2 && performance.BootSlowest.Count(b => b.CanDisable) == 1
            && performance.Top.Count(c => c.CanStopAutostart) == 1 && performance.LowDiskText is not null);
        Assert.True(performance.Settings.Count > 0 && performance.BackgroundApps.Count > 0 && performance.Top.Count == 2
            && performance.PendingCount == 1 && performance.BackgroundHeader == "Hintergrund-Apps (2 erlaubt)");
        // every link button on the page opens an allowed system link
        Assert.All(performance.Settings.Where(r => r.Link is not null), r => Assert.True(SystemLinks.IsAllowed(r.Link), r.Link));
        Assert.True(optimize.FindingCount == 4 && protect.HeuristicCount == 1 && history.Sessions.Count == 1 && update.IsAvailable
            && privacy.Rows.Count > 0);

        foreach (var theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries[1] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinWaechter.App;component/Theme/{theme}.xaml"),
            };
            var shellContent = new ShellWindow { DataContext = shell }.Content as FrameworkElement;
            Layout(shellContent);
            AssertSidebarIconShown(shellContent!);
            var overview = new OverviewView { DataContext = shell.Overview };
            Layout(overview);
            AssertFreeReadoutRightOfSuggestions(overview);
            Layout(new OptimizeView { DataContext = optimize });
            Layout(new ProtectView { DataContext = protect });
            var securityView = new SecurityView { DataContext = security };
            Layout(securityView);
            AssertLightsDiffer(securityView, theme);
            AssertSecurityButtons(securityView);
            Layout(new PrivacyView { DataContext = privacy });
            var programsView = new ProgramsView { DataContext = programs };
            Layout(programsView);
            AssertComboShowsLabel(programsView, "ProgramSortOption");
            AssertAppsSection(programsView);
            var performanceView = new PerformanceView { DataContext = performance };
            Layout(performanceView);
            AssertComboShowsLabel(performanceView, "PerfOption");
            AssertPerformanceButtons(performanceView, performance);
            // the shell has a template for the page
            shell.Navigate(performance);
            Layout(shellContent);
            Assert.NotEmpty(FindAll<PerformanceView>(shellContent!));
            shell.Navigate(shell.Overview);
            Layout(new MessageDialog("Deinstallation", "Ist die Deinstallation abgeschlossen?", null, "Ja", false, "Noch nicht").Content
                as FrameworkElement);
            var historyView = new HistoryView { DataContext = history };
            Layout(historyView);
            AssertStoreButton(historyView);
            Layout(new SettingsView { DataContext = settings });
            var dialog = new MessageDialog("Titel", "Text", ["Eins", "Zwei"], "Anwenden", final: true);
            Layout(dialog.Content as FrameworkElement);
        }
        app.Shutdown();
    }

    // 4.4: the preinstalled-apps section shows its title, one unticked box per app, the undo text and the remove button
    static void AssertAppsSection(FrameworkElement view)
    {
        var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
        Assert.Contains("Vorinstallierte Apps", texts);
        Assert.Contains("Microsoft News", texts);
        Assert.Contains(UndoLimits.StoreOnly, texts);
        Assert.Contains("Rückgängig im Verlauf", texts);
        var boxes = FindAll<CheckBox>(view).Where(c => AutomationProperties.GetName(c) is "Microsoft News" or "Copilot").ToList();
        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, c => Assert.True(c.IsChecked != true && c.IsEnabled));
        var remove = FindAll<Button>(view).Single(b => b.Content as string == "Ausgewählte entfernen (0)");
        Assert.True(remove.Visibility == Visibility.Visible && remove.ActualWidth > 0);
    }

    // 4.4: a Store-only removal shows „Im Store öffnen“ and its hint instead of an undo button
    static void AssertStoreButton(FrameworkElement view)
    {
        // every row carries the (collapsed) button; exactly one is shown
        var store = FindAll<Button>(view).Single(b => b.Content as string == "Im Store öffnen" && b.Visibility == Visibility.Visible);
        Assert.True(store.ActualWidth > 0);
        Assert.Contains(FindAll<TextBlock>(view), t => t.Text == UndoLimits.StoreOnly);
    }

    // the closed sort box shows the option's Label (DisplayMemberPath), not the record's ToString()
    static void AssertComboShowsLabel(FrameworkElement root, string recordType)
    {
        var combo = Descendants(root).OfType<ComboBox>().First();
        var texts = Descendants(combo).OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains(recordType));
        Assert.Contains(texts, t => t == ((dynamic)combo.SelectedItem).Label);
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    // all four lights, a fix row, a link row, a one-off action row and a child row with two buttons
    static SecurityViewModel Security(OptimizerEngine engine, ChangeGate gate)
    {
        var update = new SystemAction("update-signatures", "Virendefinitionen aktualisieren", "Jetzt aktualisieren", null, false,
            (_, _) => Task.FromResult(new ActionOutcome(true, "Die Virendefinitionen sind aktuell.")));
        IReadOnlyList<SecurityItem> rows =
        [
            new("defender", "Microsoft Defender", Light.Yellow, "Virendefinitionen sind 10 Tage alt", "Aktuelle Definitionen erkennen neue Schadprogramme besser.",
                SecurityActionKind.Action, "Jetzt aktualisieren", Act: update),
            new("firewall-public", "Firewall (Öffentlich)", Light.Red, "Ausgeschaltet", "Ohne Firewall sind Verbindungen von außen nicht gefiltert.",
                SecurityActionKind.Change, "Einschalten", Change: new NoChange("Firewall")),
            new("rdp", "Remotedesktop", Light.Yellow, "Eingeschaltet", "Andere Geräte können sich aus der Ferne verbinden.",
                SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.RemoteDesktop),
            new("uac", "Benutzerkontensteuerung", Light.Green, "Eingeschaltet", "Programme brauchen Ihre Zustimmung."),
            new("encryption", "Geräteverschlüsselung", Light.Grey, "Nicht verfügbar", "Der Status lässt sich nicht lesen."),
            new("listeners", "Im Netz lauschende Programme", Light.Yellow, "1 Programm lauscht", "Ein Programm nimmt Verbindungen an.",
                Children: [new("l1", "tool.exe", Light.Yellow, "Port 4444", "Nicht signiert, liegt in AppData.",
                    SecurityActionKind.DefenderScan, "Von Defender prüfen lassen", Change: new NoChange("tool"),
                    ScanPath: @"C:\Users\x\AppData\Roaming\tool.exe", ChangeLabel: "Eingehende Verbindungen sperren")]),
        ];
        return new SecurityViewModel(_ => Task.FromResult(rows), engine, new FakeDefender(), new YesDialogs(), new Shell(), canChange: false, gate);
    }

    // two rows, a pending banner (settings route for „Spiel-Tool“) and a leftover panel (launched and removed „Foo“)
    static ProgramsViewModel Programs(OptimizerEngine engine, ChangeGate gate)
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        static InstalledProgram P(string name, ProgramFlag flag, long? size) =>
            new(Hive.LocalMachine, root, name, name, "Foo GmbH", "2.1.0", new DateTime(2024, 3, 1), size,
                @"C:\Program Files\Foo\unins000.exe", @"C:\Program Files\" + name, null, false, flag);
        var foo = P("Foo", ProgramFlag.Pua, 52_428_800);
        var tool = P("Spiel-Tool", ProgramFlag.None, null);
        var lexware = P("Lexware buchhalter", ProgramFlag.Protected, 1_048_576);
        var actions = new FakeProgramActions
        {
            Programs = [foo, tool, lexware],
            PlanFor = p => p == foo
                ? new UninstallPlan(UninstallRoute.Launch, new UninstallCommand(@"C:\Program Files\Foo\unins000.exe", ""), "", false)
                : new UninstallPlan(UninstallRoute.OpenSettings, null, UninstallPlanner.MissingReason, true),
            Leftovers =
            [
                new("l1", FindingArea.Speicher, "Foo", @"C:\Program Files\Foo", ["Programmordner nach der Deinstallation"], Rating.Info, false,
                    new NoChange("Foo"), SizeBytes: 4096),
                new("l2", FindingArea.Aufgabe, "Foo Updater", @"\Foo Updater", ["Ziel fehlt"], Rating.Entbehrlich, true, new NoChange("Updater")),
            ],
        };
        // 4.4: two preinstalled apps, one with an image copy and one that only the Store can restore
        var pkgs = new FakeAppPackages()
            .Add(new AppPackage("Microsoft.BingNews", "Microsoft.BingNews_1.0.0.0_neutral__8wekyb3d8bbwe", "Microsoft.BingNews_8wekyb3d8bbwe",
                null, false, false, "Store", true))
            .Add(new AppPackage("Microsoft.Copilot", "Microsoft.Copilot_1.0.0.0_neutral__8wekyb3d8bbwe", "Microsoft.Copilot_8wekyb3d8bbwe",
                null, false, false, "Store", false));
        var vm = new ProgramsViewModel(actions, engine, new YesDialogs(), new Shell(), canChange: true, gate,
            () => new BloatAppScanner(pkgs, RuleSet.Default).Scan());
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        actions.Programs = [tool, lexware];   // the launched uninstaller removed „Foo“
        vm.UninstallCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Foo")).GetAwaiter().GetResult();
        vm.UninstallCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Spiel-Tool")).GetAwaiter().GetResult();
        Assert.True(vm.Items.Count == 2 && vm.Pending == tool && vm.ShowLeftovers && vm.Leftovers.Count == 2
            && vm.Items.Single(i => i.Name == "Spiel-Tool").CanForceRemove && vm.Apps.Count == 2);
        return vm;
    }

    // Runs inside RenderAll: only one WPF Application may exist per process, so no separate STA test.
    static void Sidebar_icon_resource_exists()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/WinWaechter.App;component/Assets/winwaechter.png"));
        Assert.NotNull(info);
        info.Stream.Dispose();
    }

    // the wordmark shows the bitmap icon, decoded at twice its size for sharp scaling
    static void AssertSidebarIconShown(FrameworkElement shell)
    {
        var icon = FindAll<System.Windows.Controls.Image>(shell).Single(i => AutomationProperties.GetName(i) == "WinWächter");
        var bitmap = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(icon.Source);
        Assert.Equal(44, bitmap.PixelWidth);
    }

    // the dot colours must come from the tone triggers: a red row and a green row differ, and neither is the grey default
    static void AssertLightsDiffer(FrameworkElement view, string theme)
    {
        var dots = FindAll<System.Windows.Shapes.Ellipse>(view).Where(e => e.Width == 10).ToList();
        System.Windows.Media.Brush Fill(string title)
        {
            var row = FindAll<TextBlock>(view).First(t => t.Text == title);
            var grid = (DependencyObject)row;
            while (grid is not Grid) grid = System.Windows.Media.VisualTreeHelper.GetParent(grid);
            return FindAll<System.Windows.Shapes.Ellipse>(grid).First().Fill;
        }
        Assert.NotEmpty(dots);
        var red = (System.Windows.Media.SolidColorBrush)Fill("Firewall (Öffentlich)");
        var green = (System.Windows.Media.SolidColorBrush)Fill("Benutzerkontensteuerung");
        var grey = (System.Windows.Media.SolidColorBrush)Fill("Geräteverschlüsselung");
        Assert.NotEqual(red.Color, green.Color);
        Assert.NotEqual(red.Color, grey.Color);
        Assert.NotEqual(green.Color, grey.Color);
    }

    // the action row shows its button; the listener child shows scan and block side by side
    static void AssertSecurityButtons(FrameworkElement view)
    {
        Button Shown(string label)
        {
            var b = FindAll<Button>(view).Single(x => x.Content as string == label);
            // laid out without a window, so IsVisible stays false: the button itself is shown and has a size
            Assert.True(b.Visibility == Visibility.Visible && b.ActualWidth > 0, label);
            return b;
        }
        Shown("Jetzt aktualisieren");
        var scan = Shown("Von Defender prüfen lassen");
        var block = Shown("Eingehende Verbindungen sperren");
        var scanAt = scan.TranslatePoint(new Point(0, 0), view);
        var blockAt = block.TranslatePoint(new Point(0, 0), view);
        Assert.Equal(scanAt.Y, blockAt.Y, 1);
        Assert.True(blockAt.X >= scanAt.X + scan.ActualWidth, $"block at {blockAt.X}, scan at {scanAt.X} (+{scan.ActualWidth})");
        // rows without a second fix show no second button
        Assert.DoesNotContain(FindAll<Button>(view), x => x.Visibility == Visibility.Visible && string.IsNullOrEmpty(x.Content as string));
    }

    // the slow start and the consumer with an autostart show „Nicht mehr mit Windows starten“, every consumer „Beenden“
    // in one aligned column, and the low-disk band its text and „Platz schaffen“
    static void AssertPerformanceButtons(FrameworkElement view, PerformanceViewModel vm)
    {
        List<Button> Shown(string label)
        {
            var list = FindAll<Button>(view).Where(x => x.Content as string == label && x.Visibility == Visibility.Visible).ToList();
            Assert.All(list, b => Assert.True(b.ActualWidth > 0, label));
            return list;
        }
        Assert.Equal(2, Shown("Nicht mehr mit Windows starten").Count); // one slow start, one consumer
        var ends = Shown("Beenden");
        Assert.Equal(2, ends.Count);
        Assert.Equal(ends[0].TranslatePoint(new Point(0, 0), view).X, ends[1].TranslatePoint(new Point(0, 0), view).X, 1);
        Assert.Single(Shown("Platz schaffen"));
        var band = FindAll<TextBlock>(view).Single(t => t.Text == vm.LowDiskText);
        Assert.True(band.ActualWidth > 0);
        Assert.Contains(FindAll<TextBlock>(view), t => t.Text == StartupMatcher.NotChangeable && t.ActualWidth > 0);
    }

    // "Wird frei" must sit in its own column right of "Offene Vorschläge", not on top of it
    static void AssertFreeReadoutRightOfSuggestions(FrameworkElement overview)
    {
        var labels = FindAll<TextBlock>(overview).ToList();
        var open = labels.Single(t => t.Text == "Offene Vorschläge");
        var free = labels.Single(t => t.Text == "Wird frei");
        double openX = open.TranslatePoint(new Point(0, 0), overview).X;
        double freeX = free.TranslatePoint(new Point(0, 0), overview).X;
        Assert.True(freeX > openX + open.ActualWidth, $"Wird frei at {freeX}, Offene Vorschläge at {openX} (+{open.ActualWidth})");
    }

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }

    static void Layout(FrameworkElement? e)
    {
        Assert.NotNull(e);
        e.Measure(new Size(1100, 760));
        e.Arrange(new Rect(0, 0, 1100, 760));
        e.UpdateLayout();
        Assert.True(e.ActualWidth > 0);
    }
}
