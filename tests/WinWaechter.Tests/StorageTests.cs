using System.Text.Json.Nodes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class StorageTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "WinWaechterTests", Guid.NewGuid().ToString("N"));
    readonly WinFileOps _ops = new();

    public StorageTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    string TempDir => Path.Combine(_root, "temp");
    SessionContext Ctx() => new(Path.Combine(_root, "session"));

    string Make(string rel, int ageDays, string content = "x")
    {
        var p = Path.Combine(TempDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
        return p;
    }

    MoveToAblageChange Change() => new(_ops, TempDir, MoveToAblageChange.DefaultMinAge);

    [Fact]
    public void Only_files_older_than_two_days_move()
    {
        var old = Make(@"sub\old.tmp", 5, "12345");
        var fresh = Make("fresh.tmp", 0);
        var ctx = Ctx();

        var rec = Change().Apply(ctx);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.Equal("1", rec.After["moved"]);
        Assert.Equal("0", rec.After["skipped"]);
        Assert.Equal("5", rec.After["bytes"]);
        Assert.Equal("temp", rec.Kind);
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "sub", "old.tmp")));
        Assert.True(File.Exists(rec.After["sidecar"]));
        Assert.Equal(DriftState.Unchanged, Change().CheckDrift(rec));
    }

    [Fact]
    public void Scanner_lists_folder_with_size_and_skips_empty_or_missing()
    {
        Make("a.tmp", 5, "123");
        Make("b.tmp", 4, "45");
        Make("new.tmp", 0, "999999");
        var user = new FakeUserContext(_root) { TempDir = TempDir };
        var findings = new TempScanner(_ops, user, Path.Combine(_root, "winTemp")).Scan();

        var f = Assert.Single(findings);
        Assert.Equal($"storage|{TempDir}", f.Id);
        Assert.Equal(FindingArea.Speicher, f.Area);
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.Equal(5, f.SizeBytes);
        Assert.Contains("2 Dateien älter als 2 Tage", f.Reasons);
        Assert.Equal("Temporäre Dateien (Benutzer)", f.Title);
    }

    [Fact]
    public void Locked_file_is_skipped_and_counted()
    {
        var locked = Make("locked.tmp", 5);
        var free = Make("free.tmp", 5);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rec = Change().Apply(Ctx());
            Assert.Equal("1", rec.After["moved"]);
            Assert.Equal("1", rec.After["skipped"]);
        }
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void Undo_restores_files_to_original_paths()
    {
        var a = Make(@"d\a.tmp", 5, "aaa");
        var ctx = Ctx();
        var change = Change();
        var rec = change.Apply(ctx);
        Assert.False(File.Exists(a));

        change.Undo(rec, ctx);

        Assert.Equal("aaa", File.ReadAllText(a));
    }

    [Fact]
    public void Undo_skips_when_original_exists_again()
    {
        var a = Make("a.tmp", 5, "old");
        var b = Make("b.tmp", 5, "bbb");
        var ctx = Ctx();
        var change = Change();
        var rec = change.Apply(ctx);
        File.WriteAllText(a, "new");

        change.Undo(rec, ctx);   // b is restored, a conflicts

        Assert.Equal("new", File.ReadAllText(a));
        Assert.Equal("bbb", File.ReadAllText(b));
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "a.tmp")));
    }

    [Fact]
    public void Undo_throws_when_nothing_could_be_restored()
    {
        var a = Make("a.tmp", 5);
        var ctx = Ctx();
        var change = Change();
        var rec = change.Apply(ctx);
        File.WriteAllText(a, "new");

        Assert.Throws<InvalidOperationException>(() => change.Undo(rec, ctx));
    }

    [Fact]
    public void ForFile_moves_single_file_regardless_of_age_and_undoes()
    {
        var f = Make("fresh.exe", 0, "mal");
        var ctx = Ctx();
        var change = MoveToAblageChange.ForFile(_ops, f);

        Assert.Equal("file", change.Kind);
        Assert.Equal(f, change.Target);
        Assert.Equal("Datei „fresh.exe“ in die Ablage verschieben", change.Description);

        var rec = change.Apply(ctx);
        Assert.False(File.Exists(f));
        Assert.Equal("1", rec.After["moved"]);

        var again = MoveToAblageChange.FromRecord(rec, _ops);
        Assert.Equal("file", again.Kind);
        again.Undo(rec, ctx);
        Assert.Equal("mal", File.ReadAllText(f));
    }

    [Fact]
    public void CheckDrift_gone_after_ablage_removed()
    {
        Make("a.tmp", 5);
        var ctx = Ctx();
        var rec = Change().Apply(ctx);
        Directory.Delete(Path.Combine(ctx.AblageDir, rec.Id), true);
        Assert.Equal(DriftState.Gone, Change().CheckDrift(rec));
    }

    [Fact]
    public void Janitor_purges_only_old_sessions()
    {
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var oldJ = store.Create("alt");
        var newJ = store.Create("neu");

        Make("a.tmp", 5, "12345");
        var oldRec = Change().Apply(oldJ.Context);
        oldJ.Upsert(oldRec);
        Make("b.tmp", 5, "123");
        var newRec = Change().Apply(newJ.Context);
        newJ.Upsert(newRec);

        // Age the first session by rewriting its journal.
        var jf = Path.Combine(oldJ.Context.SessionDir, "journal.json");
        var node = JsonNode.Parse(File.ReadAllText(jf))!;
        node["Created"] = DateTimeOffset.Now.AddDays(-20).ToString("o");
        File.WriteAllText(jf, node.ToJsonString());

        var freed = new AblageJanitor(store).Purge(AblageJanitor.DefaultMaxAge);

        Assert.Equal(5, freed);
        Assert.False(File.Exists(Path.Combine(oldJ.Context.AblageDir, oldRec.Id, "a.tmp")));
        Assert.True(File.Exists(Path.Combine(newJ.Context.AblageDir, newRec.Id, "b.tmp")));
        var sessions = store.List();
        var oldLoaded = sessions.Single(s => s.Title == "alt");
        Assert.Equal("true", oldLoaded.Records.Single().After["purged"]);
        Assert.False(sessions.Single(s => s.Title == "neu").Records.Single().After.ContainsKey("purged"));

        var freedAll = new AblageJanitor(store).PurgeNow();
        Assert.Equal(3, freedAll);
    }

    // ---- junctions on the way back / into the Ablage ----

    [Fact]
    public void Undo_skips_files_whose_target_path_runs_through_a_junction()
    {
        var fake = new FakeFileOps();
        var a = Make(@"d\a.tmp", 5, "aaa");
        var b = Make("b.tmp", 5, "bbb");
        var ctx = Ctx();
        var change = new MoveToAblageChange(fake, TempDir, MoveToAblageChange.DefaultMinAge);
        var rec = change.Apply(ctx);
        fake.Junctions.Add(Path.Combine(TempDir, "d"));   // "d" re-created as a junction meanwhile

        change.Undo(rec, ctx);

        Assert.False(File.Exists(a));
        Assert.Equal("bbb", File.ReadAllText(b));
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "d", "a.tmp")));
    }

    [Fact]
    public void Undo_throws_when_every_target_runs_through_a_junction()
    {
        var fake = new FakeFileOps();
        Make("a.tmp", 5);
        var ctx = Ctx();
        var change = new MoveToAblageChange(fake, TempDir, MoveToAblageChange.DefaultMinAge);
        var rec = change.Apply(ctx);
        fake.Junctions.Add(TempDir);   // the whole temp folder replaced by a junction

        var ex = Assert.Throws<InvalidOperationException>(() => change.Undo(rec, ctx));
        Assert.Contains("Verknüpfung", ex.Message);
        Assert.Single(fake.MoveCalls);   // only the original move, nothing moved back
    }

    [Fact]
    public void ForFile_undo_checks_the_original_parent_folder()
    {
        var fake = new FakeFileOps();
        var f = Make(@"app\x.exe", 0, "mal");
        var ctx = Ctx();
        var change = MoveToAblageChange.ForFile(fake, f);
        var rec = change.Apply(ctx);
        fake.Junctions.Add(Path.GetDirectoryName(f)!);

        Assert.Throws<InvalidOperationException>(() => change.Undo(rec, ctx));
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Apply_skips_files_below_a_junction()
    {
        var fake = new FakeFileOps();
        var a = Make(@"j\a.tmp", 5);
        var b = Make("b.tmp", 5);
        fake.Junctions.Add(Path.Combine(TempDir, "j"));

        var rec = new MoveToAblageChange(fake, TempDir, MoveToAblageChange.DefaultMinAge).Apply(Ctx());

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal("1", rec.After["moved"]);
        Assert.Equal("1", rec.After["skipped"]);
    }

    // ---- sidecar trust ----

    [Fact]
    public void Undo_refuses_an_untrusted_sidecar()
    {
        Make("a.tmp", 5);
        var dir = Path.Combine(_root, "session");
        var change = Change();
        var rec = change.Apply(new SessionContext(dir));
        var untrusted = new SessionContext(dir, _ => false);

        var ex = Assert.Throws<InvalidOperationException>(() => change.Undo(rec, untrusted));
        Assert.Contains("nicht von WinWächter", ex.Message);
    }

    [Fact]
    public void Undo_refuses_a_sidecar_outside_the_session()
    {
        Make("a.tmp", 5);
        var ctx = Ctx();
        var change = Change();
        var rec = change.Apply(ctx);
        var moved = Path.Combine(_root, "elsewhere.json");
        File.Copy(rec.After["sidecar"]!, moved);
        var forged = rec with { After = new(rec.After) { ["sidecar"] = moved } };

        Assert.Throws<InvalidOperationException>(() => change.Undo(forged, ctx));
    }

    [Fact]
    public void Store_skips_journals_that_fail_the_owner_check()
    {
        var pd = Path.Combine(_root, "pd");
        new SessionStore(pd).Create("fremd");
        Assert.Single(new SessionStore(pd).List());
        Assert.Empty(new SessionStore(pd, _ => false).List());
    }

    // ---- crash midway / nothing moved ----

    (OptimizerEngine Engine, SessionStore Store) EngineWith(FakeFileOps fake)
    {
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var factory = new WinWaechter.Core.Changes.ChangeFactory();
        factory.Register("temp", r => MoveToAblageChange.FromRecord(r, fake));
        return (new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok")), store);
    }

    static Finding TempFinding(IFileOps f, string dir) =>
        new("storage|x", FindingArea.Speicher, "Temp", dir, ["r"], Rating.Entbehrlich, true,
            new MoveToAblageChange(f, dir, MoveToAblageChange.DefaultMinAge));

    [Fact]
    public async Task Failure_between_moves_keeps_sidecar_and_undo_restores_moved_file()
    {
        var fake = new FakeFileOps();
        var a = Make("a.tmp", 5, "aaa");
        var b = Make("b.tmp", 5, "bbb");
        var (engine, store) = EngineWith(fake);
        // the second file in enumeration order "crashes"
        var order = fake.EnumerateOlderThan(TempDir, MoveToAblageChange.DefaultMinAge).Select(f => f.FullName).ToList();
        fake.Crash.Add(order[1]);

        var res = await engine.ApplyAsync("T", [TempFinding(fake, TempDir)], null, CancellationToken.None);

        Assert.Equal(1, res.Failed);
        var rec = Assert.Single(res.Session.Records);
        Assert.Equal(ChangeStatus.Failed, rec.Status);
        var sidecar = rec.After["sidecar"]!;
        Assert.Contains(order[0].Replace(@"\", @"\\"), File.ReadAllText(sidecar));
        Assert.False(File.Exists(order[0]));
        Assert.True(OptimizerEngine.IsUndoable(rec));

        // the janitor must not purge the half-done move
        Assert.Equal(0, new AblageJanitor(store).PurgeNow());
        Assert.True(Directory.Exists(rec.After["ablage"]));

        var u = engine.UndoSession(store.List().Single());
        Assert.Equal(1, u.Undone);
        Assert.Equal("aaa", File.ReadAllText(a));
        Assert.Equal("bbb", File.ReadAllText(b));
        Assert.Equal(ChangeStatus.Undone, store.List().Single().Records.Single().Status);
    }

    [Fact]
    public void Janitor_keeps_ablage_of_pending_record_and_unknown_folders()
    {
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var j = store.Create("T");
        Make("a.tmp", 5, "12345");
        var change = Change();
        var planned = change.Plan(j.Context);
        j.Upsert(planned);                       // crash right after Apply: only the Pending record exists
        change.Apply(j.Context);
        var stray = Path.Combine(j.Context.AblageDir, "stray");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "x"), "x");

        Assert.Equal(0, new AblageJanitor(store).PurgeNow());
        Assert.True(Directory.Exists(planned.After["ablage"]));
        Assert.True(File.Exists(Path.Combine(stray, "x")));
    }

    [Fact]
    public async Task Nothing_moved_is_marked_and_undo_skips_it_without_error()
    {
        var fake = new FakeFileOps();
        Make("fresh.tmp", 0);
        var (engine, store) = EngineWith(fake);

        var res = await engine.ApplyAsync("T", [TempFinding(fake, TempDir)], null, CancellationToken.None);
        var rec = Assert.Single(res.Session.Records);
        Assert.Equal("0", rec.After["moved"]);
        Assert.True(OptimizerEngine.MovedNothing(rec));
        Assert.False(OptimizerEngine.IsUndoable(rec));

        var u = engine.UndoSession(store.List().Single());
        Assert.Equal(0, u.Undone + u.Failed + u.SkippedDrift);
        Assert.Empty(u.Messages);
    }

    [Fact]
    public void Old_array_sidecar_still_undoes()
    {
        var a = Make("a.tmp", 5, "aaa");
        var ctx = Ctx();
        var change = Change();
        var rec = change.Apply(ctx);
        var stored = Path.Combine(rec.After["ablage"]!, "a.tmp");
        File.WriteAllText(rec.After["sidecar"]!,
            System.Text.Json.JsonSerializer.Serialize(new[] { new { original = a, stored } }));

        change.Undo(rec, ctx);
        Assert.Equal("aaa", File.ReadAllText(a));
    }
}
