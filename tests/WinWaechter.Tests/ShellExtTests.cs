using Microsoft.Win32;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.ContextMenu;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ShellExtTests : IDisposable
{
    const string Win = @"C:\Windows";
    const string Own = @"C:\Apps\WinWaechter";
    const string Machine = @"SOFTWARE\Classes";
    const string User = @"Software\Classes";
    static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid G3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-shellext-" + Guid.NewGuid().ToString("N"));
    readonly FakeRegistry _reg = new();
    readonly FakeSignatures _sig = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    ShellExtScanner Scanner() => new(_reg, _sig, RuleSet.Default, Win, Own);
    SessionContext Ctx() => new(Path.Combine(_root, "session"));

    // handler "name" under the location, pointing at the CLSID; the CLSID has a title and a DLL
    void Handler(string loc, string name, Guid clsid, string? dll, string? title = null, string root = Machine, Hive hive = Hive.LocalMachine)
    {
        _reg.Seed(hive, $@"{root}\{loc}\shellex\ContextMenuHandlers\{name}", "", clsid.ToString("B"));
        if (title is not null) _reg.Seed(hive, $@"{root}\CLSID\{clsid:B}", "", title);
        if (dll is not null) _reg.Seed(hive, $@"{root}\CLSID\{clsid:B}\InprocServer32", "", dll);
    }

    string Verb(string loc, string verb, string command, string root = Machine, Hive hive = Hive.LocalMachine, string? title = null)
    {
        var key = $@"{root}\{loc}\shell\{verb}";
        _reg.SeedKey(hive, key);
        if (title is not null) _reg.Seed(hive, key, "", title);
        _reg.Seed(hive, key + @"\command", "", command);
        return key;
    }

    [Fact]
    public void Third_party_handlers_and_verbs_are_listed_with_place()
    {
        Handler("*", "ZipMenu", G1, @"C:\Program Files\Zip\zipshell.dll", "Zip-Erweiterung");
        Handler("Directory", "ZipMenu", G1, @"C:\Program Files\Zip\zipshell.dll", "Zip-Erweiterung");
        Handler("Folder", "ZipMenu", G1, @"C:\Program Files\Zip\zipshell.dll", "Zip-Erweiterung");
        _sig.Signed(@"C:\Program Files\Zip\zipshell.dll", "Zip GmbH");
        Verb(@"Directory\Background", "OpenEditor", "\"C:\\Program Files\\Editor\\ed.exe\" \"%V\"", title: "Im Editor öffnen");
        _sig.Signed(@"C:\Program Files\Editor\ed.exe", "Editor AG");

        var found = Scanner().Scan();

        var h = Assert.Single(found, f => f.Id == $"shellext|{G1:B}");
        Assert.Equal("Zip-Erweiterung", h.Title);
        Assert.Equal("Dateien, Ordner", h.Location);   // "Directory" and "Folder" both read „Ordner“, merged once
        Assert.Equal(["Signatur: gültig, Herausgeber Zip GmbH", "Ort: Dateien, Ordner"], h.Reasons);
        Assert.Equal("Zip GmbH", h.Publisher);
        Assert.Equal(@"C:\Program Files\Zip\zipshell.dll", h.ExePath);
        Assert.Equal(FindingArea.Einstellung, h.Area);
        Assert.Equal(FindingGroups.ContextMenu, h.Group);
        Assert.Equal(Rating.Info, h.Rating);
        Assert.Equal("shellext", h.Change!.Kind);
        Assert.Equal("„Zip-Erweiterung“ aus dem Rechtsklick-Menü entfernen", h.Change.Description);

        var v = Assert.Single(found, f => f.Id == "shellext|HKLM|Directory\\Background|OpenEditor");
        Assert.Equal("Im Editor öffnen", v.Title);
        Assert.Equal("Hintergrund", v.Location);
        Assert.Equal("Editor AG", v.Publisher);
        Assert.Equal(@"C:\Program Files\Editor\ed.exe", v.ExePath);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Handler_in_HKCU_is_found_and_merged_with_the_machine_registration()
    {
        Handler("*", "Tool", G1, @"C:\Tools\tool.dll", "Werkzeug");
        Handler("Drive", "Tool", G1, @"C:\Tools\tool.dll", root: User, hive: Hive.CurrentUser);

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("Dateien, Laufwerke", f.Location);
    }

    [Fact]
    public void Handler_named_by_clsid_or_by_default_value_falls_back_to_a_readable_title()
    {
        // handler key is the CLSID itself and the CLSID has no title: the DLL name is used
        Handler("*", G2.ToString("B"), G2, @"C:\Tools\fancy.dll");
        // handler with a name, CLSID in the default value, no CLSID title
        Handler("*", "Mein Handler", G3, @"C:\Tools\other.dll");

        var found = Scanner().Scan();
        Assert.Contains(found, f => f.Id == $"shellext|{G2:B}" && f.Title == "fancy");
        Assert.Contains(found, f => f.Id == $"shellext|{G3:B}" && f.Title == "Mein Handler");
    }

    [Fact]
    public void Windows_microsoft_protected_and_own_entries_are_hidden()
    {
        // under the Windows folder
        Handler("*", "WinOne", G1, @"C:\Windows\System32\shell.dll", "Windows");
        // bare DLL name resolves into System32: fail closed
        Handler("*", "Bare", G2, "somewhere.dll", "Bare");
        // signed by Microsoft, outside Windows
        Handler("*", "MsTool", G3, @"C:\Program Files\Microsoft Office\ms.dll", "Office");
        _sig.Microsoft(@"C:\Program Files\Microsoft Office\ms.dll");
        // protected vendor
        var dell = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Handler("*", "DellMenu", dell, @"C:\Program Files\Dell\Menu\menu.dll", "Dell Menü");
        _sig.Signed(@"C:\Program Files\Dell\Menu\menu.dll", "Dell Inc.");
        // WinWächter's own folder
        var own = Guid.Parse("55555555-5555-5555-5555-555555555555");
        Handler("*", "Own", own, @"C:\Apps\WinWaechter\ext.dll", "Eigene");
        // no server registered at all
        var none = Guid.Parse("66666666-6666-6666-6666-666666666666");
        Handler("*", "NoDll", none, null, "Ohne DLL");

        // static verbs
        Verb("*", "open", "\"C:\\Program Files\\App\\a.exe\" \"%1\"");
        Verb("*", "runas", "\"C:\\Program Files\\App\\a.exe\" \"%1\"");
        Verb("*", "WinCmd", "\"C:\\Windows\\System32\\cmd.exe\" /c echo");
        Verb("*", "MsVerb", "\"C:\\Program Files\\Microsoft Office\\o.exe\" \"%1\"");
        _sig.Microsoft(@"C:\Program Files\Microsoft Office\o.exe");
        Verb("*", "DellVerb", "\"C:\\Program Files\\Dell\\d.exe\" \"%1\"");
        Verb("*", "OwnVerb", "\"C:\\Apps\\WinWaechter\\WinWaechter.App.exe\" \"%1\"");
        Verb("*", "BareVerb", "notepad.exe \"%1\"");
        var disabled = Verb("*", "Gone", "\"C:\\Program Files\\App\\a.exe\" \"%1\"");
        _reg.Seed(Hive.LocalMachine, disabled, "LegacyDisable", "");
        var hidden = Verb("*", "Api", "\"C:\\Program Files\\App\\a.exe\" \"%1\"");
        _reg.Seed(Hive.LocalMachine, hidden, "ProgrammaticAccessOnly", "");
        _reg.SeedKey(Hive.LocalMachine, @"SOFTWARE\Classes\*\shell\NoCommand");

        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Nothing_is_preselected()
    {
        Handler("*", "A", G1, @"C:\Tools\a.dll", "A");
        Verb("Folder", "B", "\"C:\\Tools\\b.exe\" \"%1\"");

        var found = Scanner().Scan();
        Assert.Equal(2, found.Count);
        Assert.All(found, f =>
        {
            Assert.False(f.Preselected);
            Assert.Equal(Rating.Info, f.Rating);
        });
    }

    [Fact]
    public void Already_blocked_is_not_offered()
    {
        Handler("*", "A", G1, @"C:\Tools\a.dll", "A");
        Handler("*", "B", G2, @"C:\Tools\b.dll", "B");
        _reg.Seed(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B"), "");

        var f = Assert.Single(Scanner().Scan());
        Assert.Equal($"shellext|{G2:B}", f.Id);
    }

    [Fact]
    public void Block_apply_and_undo_remove_exactly_the_value()
    {
        _reg.Seed(Hive.LocalMachine, ShellExtLocations.BlockedKey, G2.ToString("B"), "Other");
        var change = ShellExtChange.Block(_reg, G1, "Zip");

        Assert.Equal("shellext", change.Kind);
        Assert.Equal($@"HKLM\{ShellExtLocations.BlockedKey}\{G1:B}", change.Target);
        var rec = change.Apply(Ctx());

        Assert.Equal("", _reg.Get(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
        Assert.Equal(RegistryValueKind.String, _reg.GetKind(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
        Assert.Equal("false", rec.Before["existed"]);
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));
        Assert.Equal(ChangeStatus.Applied, rec.Status);

        // a second apply refuses and leaves the value as it is
        Assert.Throws<InvalidOperationException>(() => change.Apply(Ctx()));

        change.Undo(rec, Ctx());
        Assert.Null(_reg.GetKind(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
        Assert.Equal("Other", _reg.Get(Hive.LocalMachine, ShellExtLocations.BlockedKey, G2.ToString("B")));
        Assert.Equal(DriftState.Gone, change.CheckDrift(rec));
        Assert.Equal([(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B"))], _reg.DeleteCalls);
    }

    [Fact]
    public void Block_refuses_a_value_that_already_exists_and_does_not_overwrite_it()
    {
        _reg.Seed(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B"), "von jemand anderem");
        var change = ShellExtChange.Block(_reg, G1, "Zip");

        Assert.Throws<InvalidOperationException>(() => change.Apply(Ctx()));
        Assert.Equal("von jemand anderem", _reg.Get(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
        Assert.Empty(_reg.SetCalls);
    }

    [Fact]
    public void Undo_deletes_only_the_owned_value_even_when_the_journal_says_otherwise()
    {
        var change = ShellExtChange.Block(_reg, G1, "Zip");
        var rec = change.Apply(Ctx());
        var forged = rec with { Before = new() { ["existed"] = "true", ["value"] = "x", ["kind"] = "String" } };

        change.Undo(forged, Ctx());
        Assert.Null(_reg.GetKind(Hive.LocalMachine, ShellExtLocations.BlockedKey, G1.ToString("B")));
    }

    [Fact]
    public void Legacy_disable_apply_and_undo()
    {
        var key = Verb("Directory", "OpenEditor", "\"C:\\Tools\\ed.exe\" \"%1\"");
        _reg.Seed(Hive.LocalMachine, key, "MUIVerb", "Editor");
        var change = ShellExtChange.LegacyDisable(_reg, Hive.LocalMachine, key, "Editor");

        Assert.Equal($@"HKLM\{key}\LegacyDisable", change.Target);
        var rec = change.Apply(Ctx());
        Assert.Equal("", _reg.Get(Hive.LocalMachine, key, "LegacyDisable"));
        Assert.Equal("Editor", _reg.Get(Hive.LocalMachine, key, "MUIVerb"));
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));

        // now the scanner no longer offers it
        Assert.Empty(Scanner().Scan());

        change.Undo(rec, Ctx());
        Assert.Null(_reg.GetKind(Hive.LocalMachine, key, "LegacyDisable"));
        Assert.Equal("Editor", _reg.Get(Hive.LocalMachine, key, "MUIVerb"));
        Assert.Equal(DriftState.Gone, change.CheckDrift(rec));
    }

    [Fact]
    public void Legacy_disable_on_a_vanished_verb_key_refuses_and_creates_nothing()
    {
        var change = ShellExtChange.LegacyDisable(_reg, Hive.CurrentUser, $@"{User}\Folder\shell\Weg", "Weg");

        Assert.Throws<InvalidOperationException>(() => change.Apply(Ctx()));
        Assert.Empty(_reg.SetCalls);
        Assert.Empty(_reg.CreateKeyCalls);
    }

    static ChangeRecord Record(string hive, string key, string name) =>
        new("id", "shellext", "t", "d", new() { ["hive"] = hive, ["key"] = key, ["name"] = name }, new(),
            ChangeStatus.Applied, DateTimeOffset.Now);

    [Fact]
    public void FromRecord_accepts_what_the_scanner_produces_and_rebuilds_from_retry_data()
    {
        var block = ShellExtChange.Block(_reg, G1, "Zip");
        var rebuilt = ShellExtChange.FromRecord(Record("HKLM", ShellExtLocations.BlockedKey, G1.ToString("B")), _reg);
        Assert.Equal(block.Target, rebuilt.Target);

        var key = Verb("Directory", "V", "\"C:\\Tools\\v.exe\"");
        var legacy = ShellExtChange.LegacyDisable(_reg, Hive.LocalMachine, key, "V");
        var (before, after) = legacy.RetryData()!.Value;
        var again = ShellExtChange.FromRecord(new ChangeRecord("i", "shellext", legacy.Target, "d", before, after,
            ChangeStatus.Failed, DateTimeOffset.Now), _reg);
        Assert.Equal(legacy.Target, again.Target);

        var factory = new WinWaechter.Core.Changes.ChangeFactory();
        ShellExtChange.Register(factory, _reg);
        Assert.Equal("shellext", factory.Rehydrate(Record("HKLM", ShellExtLocations.BlockedKey, G1.ToString("B"))).Kind);
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "{11111111-1111-1111-1111-111111111111}")]   // other key
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked\x", "{11111111-1111-1111-1111-111111111111}")]
    [InlineData("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "{11111111-1111-1111-1111-111111111111}")]   // HKCU Blocked
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "Evil")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "11111111-1111-1111-1111-111111111111")]   // not braces
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "{11111111-1111-1111-1111-111111111111} ")]
    [InlineData("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", "")]
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\..\x", "LegacyDisable")]   // traversal
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\a\..\x", "LegacyDisable")]
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\a\b", "LegacyDisable")]   // two segments
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\a/b", "LegacyDisable")]
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\", "LegacyDisable")]   // empty verb
    [InlineData("HKLM", "SOFTWARE\\Classes\\*\\shell\\a\u0001", "LegacyDisable")]   // control char
    [InlineData("HKLM", @"SOFTWARE\Classes\Applications\x.exe\shell\open", "LegacyDisable")]   // location not on the list
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers\x", "LegacyDisable")]
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\x", "Start")]   // other value name
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\x", "")]
    [InlineData("HKXX", @"SOFTWARE\Classes\*\shell\x", "LegacyDisable")]
    public void FromRecord_rejects_unlisted(string hive, string key, string name)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ShellExtChange.FromRecord(Record(hive, key, name), _reg));
        Assert.Equal("Dieser Eintrag gehört nicht zum Rechtsklick-Menü – Rückgängig abgelehnt.", ex.Message);
    }

    [Theory]
    [InlineData("HKLM", @"SOFTWARE\Classes\*\shell\x")]
    [InlineData("HKLM", @"SOFTWARE\Classes\Directory\Background\shell\x")]
    [InlineData("HKCU", @"Software\Classes\Drive\shell\My Verb")]
    [InlineData("HKCU", @"software\classes\AllFilesystemObjects\SHELL\x")]
    public void FromRecord_accepts_listed_verb_keys(string hive, string key)
    {
        var c = ShellExtChange.FromRecord(Record(hive, key, "LegacyDisable"), _reg);
        Assert.EndsWith(@"\LegacyDisable", c.Target);
    }

    // --- Explorer restart ---

    static string Explorer => Path.Combine(Win, "explorer.exe");

    [Fact]
    public async Task Restart_kills_only_the_windows_explorer_and_starts_it_when_none_returns()
    {
        var ender = new FakeProcessEnder();
        var real = new RunningProcess("explorer.exe", Explorer, 10);
        var real2 = new RunningProcess("EXPLORER.EXE", @"c:\windows\Explorer.exe", 11);
        var fake = new RunningProcess("explorer.exe", @"C:\Users\x\Downloads\explorer.exe", 12);
        var relative = new RunningProcess("explorer.exe", "explorer.exe", 13);
        var unknown = new RunningProcess("explorer.exe", null, 14);
        ender.Running.AddRange([real, real2, fake, relative, unknown]);
        var started = new List<string>();

        var action = SystemActions.RestartExplorer(ender, started.Add, Win, TimeSpan.Zero);
        Assert.Equal("Explorer neu starten", action.Title);
        Assert.Null(action.ConfirmText);
        Assert.False(action.Final);

        var outcome = await action.RunAsync(null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal([real, real2], ender.Killed);
        Assert.Equal([fake, relative, unknown], ender.Running);
        Assert.Equal([Explorer], started);
    }

    [Fact]
    public async Task Restart_does_not_start_a_second_explorer_when_windows_brought_one_back()
    {
        var ender = new ComingBackEnder(new RunningProcess("explorer.exe", Explorer, 10));
        var started = new List<string>();

        var outcome = await SystemActions.RestartExplorer(ender, started.Add, Win, TimeSpan.Zero)
            .RunAsync(null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, ender.KillCalls);
        Assert.Empty(started);
    }

    [Fact]
    public async Task Restart_starts_the_shell_even_when_none_was_running()
    {
        var started = new List<string>();
        var outcome = await SystemActions.RestartExplorer(new FakeProcessEnder(), started.Add, Win, TimeSpan.Zero)
            .RunAsync(null, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal([Explorer], started);
    }

    // Kill removes the process, but Windows immediately starts a new explorer.
    sealed class ComingBackEnder(RunningProcess first) : IProcessEnder
    {
        readonly List<RunningProcess> _running = [first];
        public int KillCalls { get; private set; }

        public IReadOnlyList<RunningProcess> Find(string processName) => _running.ToList();
        public int CloseWindows(IReadOnlyList<RunningProcess> p) => 0;

        public int Kill(IReadOnlyList<RunningProcess> p)
        {
            KillCalls++;
            _running.Clear();
            _running.Add(new RunningProcess("explorer.exe", Explorer, 99));
            return p.Count;
        }

        public bool AnyRunning(IReadOnlyList<RunningProcess> p) => false;
    }
}
