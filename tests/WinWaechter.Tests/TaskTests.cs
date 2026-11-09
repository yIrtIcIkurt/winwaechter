using System.Runtime.InteropServices;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class TaskTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-tasks-" + Guid.NewGuid().ToString("N"));
    readonly FakeTaskStore _store = new();
    readonly FakeFileOps _files = new() { ReportAllPresent = true };
    readonly FakeSignatures _sig = new();

    public TaskTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    TaskScanner Scanner() => new(_store, _sig, RuleSet.Default, _files, new FakeUserContext(_root));
    SessionContext Ctx() => new(Path.Combine(_root, "session"));
    TaskDisableChange Change(string path = @"\CuaDriver") => new(_store, path);

    [Fact]
    public void Microsoft_folder_tasks_ignored()
    {
        _store.Add(@"\Microsoft\Windows\Foo\Bar", actionExe: @"C:\Apps\bar.exe");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Disabled_task_not_listed()
    {
        _store.Add(@"\Foo", enabled: false, actionExe: @"C:\Apps\foo.exe");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Hidden_unsigned_appdata_task_preselected_with_reasons()
    {
        var exe = @"C:\Users\x\AppData\Local\Cua\cua-driver.exe";
        _store.Add(@"\CuaDriver", hidden: true, actionExe: exe, actionArgs: "--serve");
        _sig.Unsigned(exe);
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("task|\\CuaDriver", f.Id);
        Assert.Equal(FindingArea.Aufgabe, f.Area);
        Assert.Equal("CuaDriver", f.Title);
        Assert.Equal(exe + " --serve", f.Location);
        Assert.Equal(Rating.Verdaechtig, f.Rating);
        Assert.True(f.Preselected);
        Assert.Contains("versteckt", f.Reasons);
        Assert.Contains("Ziel unsigniert", f.Reasons);
        Assert.Contains("Ziel in AppData/Temp", f.Reasons);
    }

    [Fact]
    public void Visible_unsigned_appdata_task_not_preselected()
    {
        var exe = @"C:\Users\x\AppData\Local\Foo\foo.exe";
        _store.Add(@"\Foo", actionExe: exe);
        _sig.Unsigned(exe);
        var f = Assert.Single(Scanner().Scan());
        Assert.False(f.Preselected);
        Assert.Equal(Rating.Info, f.Rating);
    }

    [Fact]
    public void Lexware_task_not_listed()
    {
        var exe = @"C:\Program Files (x86)\Lexware\Update\update.exe";
        _store.Add(@"\Lexware Update", actionExe: exe);
        _sig.Signed(exe, "Haufe-Lexware GmbH & Co. KG");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Task_under_windows_dir_not_listed()
    {
        var exe = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\System32\odd.exe";
        _store.Add(@"\Odd", actionExe: exe);
        _sig.Unsigned(exe);
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Task_without_action_listed_only_when_hidden()
    {
        _store.Add(@"\Plain");
        _store.Add(@"\Sneaky", hidden: true);
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal(@"task|\Sneaky", f.Id);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Equal("(keine Programmaktion)", f.Location);
    }

    [Fact]
    public void Quoted_env_var_exe_is_resolved_for_checks()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        var exe = Path.Combine(local, "Foo", "foo.exe");
        _store.Add(@"\Foo", hidden: true, actionExe: "\"%LOCALAPPDATA%\\Foo\\foo.exe\"");
        _sig.Unsigned(exe);
        var f = Assert.Single(Scanner().Scan());
        Assert.True(f.Preselected);
    }

    [Fact]
    public void Apply_backs_up_xml_and_disables()
    {
        _store.Add(@"\Foo\Bar", actionExe: @"C:\Apps\bar.exe");
        var rec = Change(@"\Foo\Bar").Apply(Ctx());
        Assert.False(_store.Get(@"\Foo\Bar")!.Enabled);
        var file = rec.Before["xml"]!;
        Assert.StartsWith(Ctx().BackupDir, file);
        Assert.Matches(@"^task__Foo_Bar_[0-9a-f]{8}\.xml$", Path.GetFileName(file));
        Assert.True(File.Exists(file));
        Assert.Equal("true", rec.Before["enabled"]);
        Assert.Equal("Aufgabe „Bar“ deaktivieren", rec.Description);
    }

    [Fact]
    public void Undo_enables_existing_task()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        TaskDisableChange.FromRecord(rec, _store).Undo(rec, Ctx());
        Assert.True(_store.Get(@"\Foo")!.Enabled);
    }

    [Fact]
    public void Undo_reregisters_deleted_task_from_backup()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        _store.Remove(@"\Foo");

        TaskDisableChange.FromRecord(rec, _store).Undo(rec, Ctx());

        var call = Assert.Single(_store.RegisterXmlCalls);
        Assert.Equal(@"\Foo", call.Path);
        Assert.Equal(File.ReadAllText(rec.Before["xml"]!), call.Xml);
        Assert.True(_store.Get(@"\Foo")!.Enabled);
    }

    [Fact]
    public void Undo_of_deleted_task_with_stored_password_gives_clear_error()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        File.WriteAllText(rec.Before["xml"]!, "<Task><LogonType>Password</LogonType></Task>");
        _store.Remove(@"\Foo");
        _store.RegisterXmlThrows = new COMException("kein Kennwort");

        var ex = Assert.Throws<InvalidOperationException>(() => Change(@"\Foo").Undo(rec, Ctx()));
        Assert.Contains("gespeichertes Kennwort", ex.Message);
        Assert.Contains(rec.Before["xml"]!, ex.Message);
    }

    [Fact]
    public void Undo_of_deleted_task_without_backup_fails()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        _store.Remove(@"\Foo");
        File.Delete(rec.Before["xml"]!);
        Assert.Throws<InvalidOperationException>(() => Change(@"\Foo").Undo(rec, Ctx()));
    }

    [Fact]
    public void Drift_when_reenabled()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var change = Change(@"\Foo");
        var rec = change.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));
        _store.SetEnabled(@"\Foo", true);
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));
        _store.Remove(@"\Foo");
        Assert.Equal(DriftState.Gone, change.CheckDrift(rec));
    }

    [Fact]
    public void Backup_is_utf16_with_bom_and_round_trips()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var expected = _store.ExportXml(@"\Foo");
        var rec = Change(@"\Foo").Apply(Ctx());
        var bytes = File.ReadAllBytes(rec.Before["xml"]!);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
        Assert.Equal(expected, File.ReadAllText(rec.Before["xml"]!));
    }

    [Fact]
    public void Backup_names_differ_for_paths_that_sanitize_equally()
    {
        _store.Add(@"\A\B", actionExe: @"C:\Apps\a.exe");
        _store.Add(@"\A_B", actionExe: @"C:\Apps\b.exe");
        var r1 = Change(@"\A\B").Apply(Ctx());
        var r2 = Change(@"\A_B").Apply(Ctx());
        Assert.NotEqual(r1.Before["xml"], r2.Before["xml"]);
    }

    [Fact]
    public void Hidden_protected_task_without_action_not_listed()
    {
        _store.Add(new TaskInfo(@"\Dell SupportAssist", "Dell SupportAssist", true, true, null, null, "Dell Inc."));
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Register_failure_without_password_logon_shows_exception_message()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        _store.Remove(@"\Foo");
        _store.RegisterXmlThrows = new COMException("kaputt");
        var ex = Assert.Throws<InvalidOperationException>(() => Change(@"\Foo").Undo(rec, Ctx()));
        Assert.Contains("kaputt", ex.Message);
        Assert.DoesNotContain("Kennwort", ex.Message);
    }

    [Fact]
    public void Register_failure_with_password_logon_mentions_password()
    {
        _store.Add(@"\Foo", actionExe: @"C:\Apps\foo.exe");
        var rec = Change(@"\Foo").Apply(Ctx());
        File.WriteAllText(rec.Before["xml"]!, "<Task><LogonType>Password</LogonType></Task>");
        _store.Remove(@"\Foo");
        _store.RegisterXmlThrows = new COMException("x");
        var ex = Assert.Throws<InvalidOperationException>(() => Change(@"\Foo").Undo(rec, Ctx()));
        Assert.Contains("gespeichertes Kennwort", ex.Message);
    }

    Finding One(string name, string exe, SignatureInfo sig)
    {
        _store.Add(new TaskInfo(@"\" + name, name, true, false, exe, null, null));
        _sig.Set(exe, sig);
        return Assert.Single(Scanner().Scan());
    }

    [Fact]
    public void Pua_task_is_unerwuenscht_and_preselected()
    {
        var f = One("Glary Utilities", @"C:\Program Files\Glary Utilities\gu.exe", new(SignatureState.Valid, "Glarysoft Ltd", false));
        Assert.Equal(Rating.Unerwuenscht, f.Rating);
        Assert.True(f.Preselected);
    }

    [Fact]
    public void Miner_task_is_verdaechtig_and_not_preselected()
    {
        var f = One("xmrig", @"C:\ProgramData\miner\xmrig.exe", new(SignatureState.Valid, "Someone", false));
        Assert.Equal(Rating.Verdaechtig, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public void Unknown_signature_task_is_info_not_preselected()
    {
        var f = One("Foo", @"C:\Apps\foo.exe", new(SignatureState.Unknown, null, false));
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Contains("Signatur nicht prüfbar", f.Reasons);
    }
}
