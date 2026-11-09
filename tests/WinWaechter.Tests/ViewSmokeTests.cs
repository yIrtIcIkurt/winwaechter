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
using WinWaechter.Core.Security.Defender;
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
        var history = new HistoryViewModel(store, engine, new AblageJanitor(store), dialogs, canChange: false, gate);
        var env = new AppEnvironment(false, _root, new SemVer(0, 1, 0));
        var update = new UpdateViewModel(new Updates(), dialogs, canInstall: false, () => { }, gate);
        var settings = new SettingsViewModel(new AppSettings(), env, update, new Shell());
        var shell = new ShellViewModel(optimize, protect, history, settings, update, isElevated: false);

        // fill the view models (no sync context here, so the commands finish inline or on the pool)
        optimize.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        protect.HeuristicScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        history.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        update.CheckCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Assert.True(optimize.FindingCount == 4 && protect.HeuristicCount == 1 && history.Sessions.Count == 1 && update.IsAvailable);

        foreach (var theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries[1] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinWaechter.App;component/Theme/{theme}.xaml"),
            };
            Layout(new ShellWindow { DataContext = shell }.Content as FrameworkElement);
            Layout(new OverviewView { DataContext = shell.Overview });
            Layout(new OptimizeView { DataContext = optimize });
            Layout(new ProtectView { DataContext = protect });
            Layout(new HistoryView { DataContext = history });
            Layout(new SettingsView { DataContext = settings });
            var dialog = new MessageDialog("Titel", "Text", ["Eins", "Zwei"], "Anwenden", final: true);
            Layout(dialog.Content as FrameworkElement);
        }
        app.Shutdown();
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
