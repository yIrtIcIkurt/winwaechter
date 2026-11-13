using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Rules;
using Xunit;

namespace WinWaechter.Tests;

public class StartupMatcherTests
{
    sealed class NoopChange : IChange
    {
        public string Kind => "autostart";
        public string Target => "t";
        public string Description => "d";
        public ChangeRecord Apply(SessionContext ctx) => throw new NotSupportedException();
        public void Undo(ChangeRecord record, SessionContext ctx) => throw new NotSupportedException();
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    static Finding F(FindingArea area, string title, string? exe, bool change = true, bool prot = false) =>
        new("id-" + title, area, title, "loc", [], Rating.Entbehrlich, false, change ? new NoopChange() : null,
            IsProtectedVendor: prot, ExePath: exe);

    static readonly RuleSet Rules = RuleSet.Default;

    [Fact]
    public void Autostart_task_and_service_are_matched_by_file_name()
    {
        var auto = F(FindingArea.Autostart, "Foo Updater", @"C:\Users\x\AppData\Foo\foo.exe");
        var task = F(FindingArea.Aufgabe, "Bar Task", @"C:\Tools\bar.exe");
        var svc = F(FindingArea.Dienst, "Baz Dienst", @"C:\Tools\baz.exe");
        var other = F(FindingArea.Speicher, "Cache", @"C:\Tools\qux.exe");
        var list = new[] { auto, task, svc, other };

        Assert.Same(auto, StartupMatcher.Match(new("Foo", TimeSpan.FromSeconds(5), "FOO.EXE"), list, Rules).Finding);
        Assert.Same(task, StartupMatcher.Match(new("Bar", TimeSpan.FromSeconds(5), "bar.exe"), list, Rules).Finding);
        Assert.Same(svc, StartupMatcher.Match(new("irgendwas", TimeSpan.FromSeconds(5), "baz.exe"), list, Rules).Finding);
        Assert.Same(auto, StartupMatcher.Match(new("foo updater", TimeSpan.FromSeconds(5)), list, Rules).Finding);
        // wrong area is not a candidate
        Assert.Null(StartupMatcher.Match(new("x", TimeSpan.FromSeconds(5), "qux.exe"), list, Rules).Finding);
    }

    [Fact]
    public void Disabled_autostart_says_already_off()
    {
        var off = F(FindingArea.Autostart, "Foo", @"C:\Tools\foo.exe", change: false);
        var m = StartupMatcher.Match(new("Foo", TimeSpan.FromSeconds(5), "foo.exe"), [off], Rules);
        Assert.Null(m.Finding);
        Assert.Equal(StartupMatcher.AlreadyOff, m.Note);
    }

    [Fact]
    public void Dell_is_protected_without_button()
    {
        var dell = F(FindingArea.Autostart, "Dell Update", @"C:\Program Files\Dell\du.exe", prot: true);
        var m = StartupMatcher.Match(new("Dell Update", TimeSpan.FromSeconds(5), "du.exe"), [dell], Rules);
        Assert.Null(m.Finding);
        Assert.Equal(StartupMatcher.ProtectedNote, m.Note);

        var none = StartupMatcher.Match(new("DellClientManagementService", TimeSpan.FromSeconds(5)), [], Rules);
        Assert.Null(none.Finding);
        Assert.Equal(StartupMatcher.ProtectedNote, none.Note);
    }

    [Fact]
    public void Unknown_is_windows_or_driver()
    {
        var m = StartupMatcher.Match(new("ntoskrnl", TimeSpan.FromSeconds(5), "xyzdriver.sys"), [], Rules);
        Assert.Null(m.Finding);
        Assert.Equal(StartupMatcher.NotChangeable, m.Note);
    }
}
