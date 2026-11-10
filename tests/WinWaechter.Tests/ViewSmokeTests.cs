using System.Windows;
using System.Windows.Controls;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.App.Views;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
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
        var history = new HistoryViewModel(store, engine, new AblageJanitor(store), dialogs, canChange: false, gate);
        var env = new AppEnvironment(false, _root, new SemVer(0, 1, 0));
        var update = new UpdateViewModel(new Updates(), dialogs, canInstall: false, () => { }, gate);
        var settings = new SettingsViewModel(new AppSettings(), env, update, new Shell());
        var programs = Programs(engine, gate);
        var security = Security(engine, gate);
        var shell = new ShellViewModel(optimize, protect, security, privacy, programs, history, settings, update, isElevated: false);

        // fill the view models (no sync context here, so the commands finish inline or on the pool)
        optimize.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.HeuristicScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        history.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        privacy.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        security.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        privacy.SelectRecommendedCommand.Execute(null);
        update.CheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(optimize.FindingCount == 4 && protect.HeuristicCount == 1 && history.Sessions.Count == 1 && update.IsAvailable
            && privacy.Rows.Count > 0);

        foreach (var theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries[1] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinWaechter.App;component/Theme/{theme}.xaml"),
            };
            Layout(new ShellWindow { DataContext = shell }.Content as FrameworkElement);
            var overview = new OverviewView { DataContext = shell.Overview };
            Layout(overview);
            AssertFreeReadoutRightOfSuggestions(overview);
            Layout(new OptimizeView { DataContext = optimize });
            Layout(new ProtectView { DataContext = protect });
            var securityView = new SecurityView { DataContext = security };
            Layout(securityView);
            AssertLightsDiffer(securityView, theme);
            Layout(new PrivacyView { DataContext = privacy });
            Layout(new ProgramsView { DataContext = programs });
            Layout(new MessageDialog("Deinstallation", "Ist die Deinstallation abgeschlossen?", null, "Ja", false, "Noch nicht").Content
                as FrameworkElement);
            Layout(new HistoryView { DataContext = history });
            Layout(new SettingsView { DataContext = settings });
            var dialog = new MessageDialog("Titel", "Text", ["Eins", "Zwei"], "Anwenden", final: true);
            Layout(dialog.Content as FrameworkElement);
        }
        app.Shutdown();
    }

    // all four lights, one action row and one child row
    static SecurityViewModel Security(OptimizerEngine engine, ChangeGate gate)
    {
        IReadOnlyList<SecurityItem> rows =
        [
            new("firewall-public", "Firewall (Öffentlich)", Light.Red, "Ausgeschaltet", "Ohne Firewall sind Verbindungen von außen nicht gefiltert.",
                SecurityActionKind.Change, "Einschalten", Change: new NoChange("Firewall")),
            new("rdp", "Remotedesktop", Light.Yellow, "Eingeschaltet", "Andere Geräte können sich aus der Ferne verbinden.",
                SecurityActionKind.Link, "Einstellungen öffnen", SystemLinks.RemoteDesktop),
            new("uac", "Benutzerkontensteuerung", Light.Green, "Eingeschaltet", "Programme brauchen Ihre Zustimmung."),
            new("encryption", "Geräteverschlüsselung", Light.Grey, "Nicht verfügbar", "Der Status lässt sich nicht lesen."),
            new("listeners", "Im Netz lauschende Programme", Light.Yellow, "1 Programm lauscht", "Ein Programm nimmt Verbindungen an.",
                Children: [new("l1", "tool.exe", Light.Yellow, "Port 4444", "Nicht signiert, liegt in AppData.",
                    SecurityActionKind.DefenderScan, "Von Defender prüfen lassen", ScanPath: @"C:\Users\x\AppData\Roaming\tool.exe")]),
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
        var vm = new ProgramsViewModel(actions, engine, new YesDialogs(), new Shell(), canChange: true, gate);
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        actions.Programs = [tool, lexware];   // the launched uninstaller removed „Foo“
        vm.UninstallCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Foo")).GetAwaiter().GetResult();
        vm.UninstallCommand.ExecuteAsync(vm.Items.Single(i => i.Name == "Spiel-Tool")).GetAwaiter().GetResult();
        Assert.True(vm.Items.Count == 2 && vm.Pending == tool && vm.ShowLeftovers && vm.Leftovers.Count == 2
            && vm.Items.Single(i => i.Name == "Spiel-Tool").CanForceRemove);
        return vm;
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
