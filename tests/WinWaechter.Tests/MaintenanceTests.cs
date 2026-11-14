using System.Xml.Linq;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Maintenance;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Tests;

public sealed class MaintenanceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ww-maint").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void Task_xml_has_weekly_trigger_highest_interactive_catch_up_and_limit()
    {
        var xml = MaintenanceTask.Xml(@"C:\Program Files\WinWaechter\WinWaechter.App.exe", "S-1-5-21-1-2-3-1001", DayOfWeek.Sunday,
            new TimeOnly(11, 0), new DateTime(2026, 11, 15));
        var doc = XDocument.Parse(xml);
        var trigger = doc.Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("2026-11-15T11:00:00", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.NotNull(trigger.Descendants(Ns + "Sunday").SingleOrDefault());
        Assert.Equal("1", trigger.Descendants(Ns + "WeeksInterval").Single().Value);
        var settings = doc.Descendants(Ns + "Settings").Single();
        Assert.Equal("true", settings.Element(Ns + "StartWhenAvailable")!.Value);
        Assert.Equal("true", settings.Element(Ns + "RunOnlyIfNetworkAvailable")!.Value);
        Assert.Equal("PT30M", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("7", settings.Element(Ns + "Priority")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        var principal = doc.Descendants(Ns + "Principal").Single();
        Assert.Equal("S-1-5-21-1-2-3-1001", principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        var exec = doc.Descendants(Ns + "Exec").Single();
        Assert.Equal(@"C:\Program Files\WinWaechter\WinWaechter.App.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("--pruefen", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal("WinWächter", doc.Descendants(Ns + "Author").Single().Value);
    }

    [Fact]
    public void Register_and_remove_use_the_fixed_path()
    {
        var store = new Fakes.FakeTaskStore();
        MaintenanceTask.Register(store, @"C:\x.exe", "S-1-5-21-9", DayOfWeek.Monday, new TimeOnly(9, 0), new DateTime(2026, 11, 14));
        Assert.Equal(@"\WinWaechter\Wöchentliche Prüfung", store.RegisterXmlCalls.Single().Path);
        MaintenanceTask.Remove(store);
        Assert.Equal([@"\WinWaechter\Wöchentliche Prüfung"], store.DeleteCalls);
    }

    [Fact]
    public void Next_start_is_the_coming_weekday()
    {
        Assert.Equal(new DateTime(2026, 11, 15), MaintenanceTask.NextDate(new DateTime(2026, 11, 14, 20, 0, 0), DayOfWeek.Sunday));
        Assert.Equal(new DateTime(2026, 11, 14), MaintenanceTask.NextDate(new DateTime(2026, 11, 14, 8, 0, 0), DayOfWeek.Saturday));
    }

    static CheckResult R(int improvements = 2, int actions = 0, int updates = 3, bool failed = false, bool app = false, string[]? titles = null,
        DateTimeOffset? at = null) =>
        new(at ?? new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.FromHours(1)), failed, improvements, actions, updates, app,
            titles ?? ["Temp-Dateien", "7-Zip"]);

    [Fact]
    public void Notify_rule_cases()
    {
        var now = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.FromHours(1));
        Assert.False(NotifyRule.ShouldNotify(R(0, 0, 0), null, now));                          // nothing to do
        Assert.True(NotifyRule.ShouldNotify(R(0, 0, 0, app: true), null, now));                // only an app update
        Assert.True(NotifyRule.ShouldNotify(R(), null, now));                                  // first time
        Assert.False(NotifyRule.ShouldNotify(R(), R(at: now.AddDays(-6)), now));               // same result within 7 days
        Assert.True(NotifyRule.ShouldNotify(R(), R(at: now.AddDays(-7)), now));                // same result after 7 days
        Assert.True(NotifyRule.ShouldNotify(R(updates: 4), R(at: now.AddDays(-1)), now));      // changed counts
        Assert.True(NotifyRule.ShouldNotify(R(titles: ["Neu"]), R(at: now.AddDays(-1)), now)); // changed titles
        Assert.False(NotifyRule.ShouldNotify(R(failed: true), null, now));                     // failed check
        Assert.False(NotifyRule.ShouldNotify(R(titles: ["7-Zip", "Temp-Dateien"]), R(at: now.AddDays(-1)), now)); // order does not matter
    }

    [Fact]
    public void Toast_text_plural_singular_and_escaping()
    {
        Assert.Equal("6 Empfehlungen, 12 Programm-Updates", ToastContent.Text(R(5, 1, 12)));
        Assert.Equal("1 Empfehlung, 1 Programm-Update", ToastContent.Text(R(1, 0, 1)));
        Assert.Equal("3 Empfehlungen", ToastContent.Text(R(3, 0, 0)));
        Assert.Equal("2 Programm-Updates, WinWächter-Update verfügbar", ToastContent.Text(R(0, 0, 2, app: true)));
        var xml = ToastContent.Xml("<b>&", "x > y");
        Assert.Contains("&lt;b&gt;&amp;", xml);
        Assert.Contains("x &gt; y", xml);
        XDocument.Parse(xml);
    }

    [Fact]
    public void Toast_xml_uses_protocol_activation()
    {
        var doc = XDocument.Parse(ToastContent.Xml("WinWächter", "3 Empfehlungen"));
        var toast = doc.Root!;
        Assert.Equal("winwaechter:verbessern", (string?)toast.Attribute("launch"));
        Assert.Equal("protocol", (string?)toast.Attribute("activationType"));
        var action = doc.Descendants("action").Single();
        Assert.Equal("Ansehen", (string?)action.Attribute("content"));
        Assert.Equal("winwaechter:verbessern", (string?)action.Attribute("arguments"));
        Assert.Equal("protocol", (string?)action.Attribute("activationType"));
        Assert.Equal(["WinWächter", "3 Empfehlungen"], doc.Descendants("text").Select(t => t.Value));
    }

    // a change that would fail the test if anything applied it
    sealed class Tripwire : IChange
    {
        public int Applies;
        public string Kind => "trip";
        public string Target => "t";
        public string Description => "Temp-Dateien";
        public ChangeRecord Apply(SessionContext ctx) { Applies++; throw new InvalidOperationException("must not apply"); }
        public void Undo(ChangeRecord record, SessionContext ctx) => throw new InvalidOperationException();
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    MaintenanceCheck Check(ImprovementCollector collector, List<(string, string)> notified, Func<CancellationToken, Task<int>>? updates = null,
        DateTimeOffset? now = null) =>
        new(collector, updates ?? (_ => Task.FromResult(2)), _ => Task.FromResult(false), _dir, (t, x) => notified.Add((t, x)),
            () => now ?? new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.FromHours(1)));

    [Fact]
    public async Task Check_makes_no_change_and_notifies_once()
    {
        var trip = new Tripwire();
        var collector = new ImprovementCollector([new ImprovementSource("Optimieren", _ => Task.FromResult<(IReadOnlyList<Improvement>, IReadOnlyList<ActionSuggestion>)>(
            ([new Improvement("temp", ImprovementArea.Aufraeumen, "Temp-Dateien", "weil", [trip])], [])))]);
        var notified = new List<(string, string)>();
        var r = await Check(collector, notified).RunAsync(CancellationToken.None);
        Assert.Equal(0, trip.Applies);
        Assert.False(r.Failed);
        Assert.Equal((1, 2), (r.Improvements, r.Updates));
        Assert.Equal([("WinWächter", "1 Empfehlung, 2 Programm-Updates")], notified);
        var saved = CheckResultStore.Load(Path.Combine(_dir, "wartung", "letzte-pruefung.json"))!;
        Assert.Equal((r.At, r.Improvements, r.Updates), (saved.At, saved.Improvements, saved.Updates));
        Assert.Equal(r.Titles, saved.Titles);
        // the same result an hour later: no second notification
        await Check(collector, notified, now: new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(1))).RunAsync(CancellationToken.None);
        Assert.Single(notified);
    }

    [Fact]
    public async Task Failed_collection_sets_Failed_and_does_not_notify()
    {
        var collector = new ImprovementCollector([]);
        var notified = new List<(string, string)>();
        var r = await Check(collector, notified, updates: _ => throw new InvalidOperationException("winget kaputt")).RunAsync(CancellationToken.None);
        Assert.True(r.Failed);
        Assert.Empty(notified);
        Assert.True(CheckResultStore.Load(Path.Combine(_dir, "wartung", "letzte-pruefung.json"))!.Failed);
    }

    [Fact]
    public async Task Second_check_exits_and_result_is_written_atomically()
    {
        var path = Path.Combine(_dir, "wartung", "letzte-pruefung.json");
        CheckResultStore.Save(path, R());
        var reads = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) Assert.NotNull(CheckResultStore.Load(path));
        });
        for (var i = 0; i < 200; i++) CheckResultStore.Save(path, R(updates: i));
        await reads;
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Null(CheckResultStore.Load(Path.Combine(_dir, "fehlt.json")));
        File.WriteAllText(Path.Combine(_dir, "kaputt.json"), "{");
        Assert.Null(CheckResultStore.Load(Path.Combine(_dir, "kaputt.json")));
    }

    static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinWaechter.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void Notify_project_links_toast_content_and_targets_windows_sdk()
    {
        var csproj = XDocument.Load(RepoFile("src", "WinWaechter.Notify", "WinWaechter.Notify.csproj"));
        Assert.Equal("net10.0-windows10.0.19041.0", csproj.Descendants("TargetFramework").Single().Value);
        Assert.Contains(csproj.Descendants("Compile"), c => (string?)c.Attribute("Include") == @"..\WinWaechter.Core\Maintenance\ToastXml.cs");
        Assert.Empty(csproj.Descendants("PackageReference"));
        Assert.Contains("asInvoker", File.ReadAllText(RepoFile("src", "WinWaechter.Notify", "app.manifest")));
        // the installer registers the same AppUserModelID, the protocol and removes the task
        var iss = File.ReadAllText(RepoFile("installer", "winwaechter.iss"));
        Assert.Contains($"AppUserModelID: \"{ToastXml.AppId}\"", iss);
        Assert.Contains(@"Software\Classes\winwaechter", iss);
        Assert.Contains(MaintenanceTask.Path, iss);
        Assert.Contains("WinWaechter.Notify", File.ReadAllText(RepoFile("build-release.ps1")));
    }

    [Theory]
    [InlineData("einfach", "\"einfach\"")]
    [InlineData("mit \"Zitat\"", "\"mit \\\"Zitat\\\"\"")]
    [InlineData(@"endet\", "\"endet\\\\\"")]
    public void Command_line_quote(string raw, string quoted)
    {
        Assert.Equal(quoted, CommandLineArgs.Quote(raw));
    }
}
