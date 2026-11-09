using System.Text.Json.Nodes;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class AblageKindsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "WinWaechterTests", Guid.NewGuid().ToString("N"));
    readonly WinFileOps _ops = new();

    public AblageKindsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, "session"));

    string Make(string rel, int writeAgeDays, string content = "x", int? creationAgeDays = null)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (creationAgeDays is { } c) File.SetCreationTimeUtc(p, DateTime.UtcNow.AddDays(-c));
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-writeAgeDays));
        return p;
    }

    string P(string rel) => Path.Combine(_root, rel);

    // ---- cache ----

    [Fact]
    public void ForCache_moves_only_included_dirs_and_keeps_relative_layout()
    {
        var f1 = Make(@"ud\Default\Cache\f1", 1, "cache");
        var prefs = Make(@"ud\Default\Preferences", 1, "prefs");
        var ctx = Ctx();
        var include = P(@"ud\Default\Cache");
        var change = MoveToAblageChange.ForCache(_ops, P("ud"), [include], TimeSpan.Zero);

        var rec = change.Apply(ctx);

        Assert.Equal("cache", rec.Kind);
        Assert.Equal(include, rec.Before["include"]);
        Assert.Equal(P("ud"), rec.Before["path"]);
        Assert.Equal("1", rec.After["moved"]);
        Assert.False(File.Exists(f1));
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "Default", "Cache", "f1")));
        Assert.Equal("prefs", File.ReadAllText(prefs));

        MoveToAblageChange.FromRecord(rec, _ops).Undo(rec, ctx);
        Assert.Equal("cache", File.ReadAllText(f1));
    }

    [Fact]
    public void ForCache_ageByNewer_uses_newer_of_creation_and_write()
    {
        var fresh = Make(@"c\fresh", 10, "a");                     // created now, written 10 days ago
        var old = Make(@"c\old", 10, "b", creationAgeDays: 10);
        var change = MoveToAblageChange.ForCache(_ops, P("c"), [P("c")], TimeSpan.FromDays(7), ageByNewerTimestamp: true);

        Assert.Equal((1, 1L), change.Measure());
        var rec = change.Apply(Ctx());

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(old));
        Assert.Equal("1", rec.After["moved"]);
    }

    [Fact]
    public void ForCache_rejects_include_outside_root()
    {
        Directory.CreateDirectory(P("ud"));
        Assert.Throws<ArgumentException>(() =>
            MoveToAblageChange.ForCache(_ops, P("ud"), [P("other")], TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() =>
            MoveToAblageChange.ForCache(_ops, P("ud"), [P(@"ud\..\other")], TimeSpan.Zero));
    }

    [Fact]
    public void ForCacheFile_records_single_and_rehydrates_as_single()
    {
        var dump = Make(@"windows\MEMORY.DMP", 0, "dump");
        var ctx = Ctx();
        var change = MoveToAblageChange.ForCacheFile(_ops, dump);
        Assert.Equal((1, 4L), change.Measure());

        var rec = change.Apply(ctx);

        Assert.Equal("cache", rec.Kind);
        Assert.Equal("true", rec.Before["single"]);
        Assert.False(File.Exists(dump));
        MoveToAblageChange.FromRecord(rec, _ops).Undo(rec, ctx);
        Assert.Equal("dump", File.ReadAllText(dump));
    }

    // ---- folder ----

    string MakeProgramFolder()
    {
        Make(@"pf\Foo\app.exe", 30, "12345");
        Make(@"pf\Foo\sub\data.bin", 30, "123");
        return P(@"pf\Foo");
    }

    [Fact]
    public void ForFolder_moves_whole_folder_in_one_step_and_undo_restores()
    {
        var foo = MakeProgramFolder();
        var fake = new FakeFileOps();
        var ctx = Ctx();
        var change = MoveToAblageChange.ForFolder(fake, foo);
        Assert.Equal("folder", change.Kind);
        Assert.Equal((2, 8L), change.Measure());

        var rec = change.Apply(ctx);

        Assert.Single(fake.DirMoveCalls);
        Assert.Empty(fake.MoveCalls);
        Assert.Equal("folder", rec.Kind);
        Assert.Equal("1", rec.After["moved"]);
        Assert.Equal("8", rec.After["bytes"]);
        Assert.False(Directory.Exists(foo));
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "Foo", "sub", "data.bin")));
        Assert.False(OptimizerEngine.MovedNothing(rec));

        MoveToAblageChange.FromRecord(rec, fake).Undo(rec, ctx);
        Assert.Equal("12345", File.ReadAllText(Path.Combine(foo, "app.exe")));
        Assert.Equal("123", File.ReadAllText(Path.Combine(foo, "sub", "data.bin")));
    }

    [Fact]
    public void ForFolder_failure_moves_nothing_and_reports_reason()
    {
        var foo = MakeProgramFolder();
        var fake = new FakeFileOps();
        fake.LockedDirs.Add(foo);

        var rec = MoveToAblageChange.ForFolder(fake, foo).Apply(Ctx());   // R7: no throw, engine continues

        Assert.Equal("0", rec.After["moved"]);
        Assert.Contains("„Foo“", rec.After["reason"]);
        Assert.Contains("nichts verschoben", rec.After["reason"]);
        Assert.True(File.Exists(Path.Combine(foo, "app.exe")));
        Assert.Equal("", File.ReadAllText(rec.After["sidecar"]!));
        Assert.True(OptimizerEngine.MovedNothing(rec));
        Assert.False(OptimizerEngine.IsUndoable(rec));
    }

    [Fact]
    public void ForFolder_undo_reports_conflict_when_original_exists_again()
    {
        var foo = MakeProgramFolder();
        var ctx = Ctx();
        var rec = MoveToAblageChange.ForFolder(_ops, foo).Apply(ctx);
        Directory.CreateDirectory(foo);

        var ex = Assert.Throws<InvalidOperationException>(() => MoveToAblageChange.FromRecord(rec, _ops).Undo(rec, ctx));

        Assert.Contains("existiert wieder", ex.Message);
        Assert.True(File.Exists(Path.Combine(ctx.AblageDir, rec.Id, "Foo", "app.exe")));
    }

    [Fact]
    public void ForFolder_refuses_junction_folder()
    {
        var foo = MakeProgramFolder();
        var fake = new FakeFileOps();
        fake.Junctions.Add(foo);

        var rec = MoveToAblageChange.ForFolder(fake, foo).Apply(Ctx());

        Assert.Empty(fake.DirMoveCalls);
        Assert.Equal("0", rec.After["moved"]);
        Assert.Contains("nichts verschoben", rec.After["reason"]);
        Assert.True(Directory.Exists(foo));
    }

    [Fact]
    public async Task Engine_continues_after_failed_folder_move()
    {
        var foo = MakeProgramFolder();
        var bar = Make(@"pf\Bar\x.dll", 30, "xy");
        var fake = new FakeFileOps();
        fake.LockedDirs.Add(foo);
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var factory = new ChangeFactory();
        factory.Register("folder", r => MoveToAblageChange.FromRecord(r, fake));
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"));
        Finding F(string dir) => new($"leftover|{dir}", FindingArea.Speicher, "Ordner", dir, ["r"], Rating.Entbehrlich, false,
            MoveToAblageChange.ForFolder(fake, dir), Group: FindingGroups.OrphanFolders);

        var res = await engine.ApplyAsync("T", [F(foo), F(P(@"pf\Bar"))], null, CancellationToken.None);

        Assert.Equal(2, res.Applied);
        Assert.Equal(0, res.Failed);
        Assert.True(Directory.Exists(foo));
        Assert.False(File.Exists(bar));
        var u = engine.UndoSession(store.List().Single());
        Assert.Equal(1, u.Undone);
        Assert.Equal("xy", File.ReadAllText(bar));
    }

    // ---- janitor ----

    (SessionStore Store, SessionJournal J, ChangeRecord Cache, ChangeRecord Folder, ChangeRecord Temp) SessionWithAllKinds()
    {
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var j = store.Create("T");
        Make(@"ud\Cache\c1", 0, "12");
        var cache = MoveToAblageChange.ForCache(_ops, P("ud"), [P(@"ud\Cache")], TimeSpan.Zero).Apply(j.Context);
        j.Upsert(cache);
        var folder = MoveToAblageChange.ForFolder(_ops, MakeProgramFolder()).Apply(j.Context);
        j.Upsert(folder);
        Make(@"temp\t1", 5, "123");
        var temp = new MoveToAblageChange(_ops, P("temp"), MoveToAblageChange.DefaultMinAge).Apply(j.Context);
        j.Upsert(temp);
        return (store, j, cache, folder, temp);
    }

    [Fact]
    public void Auto_purge_removes_cache_after_one_day_keeps_folder_kind()
    {
        var (store, j, cache, folder, temp) = SessionWithAllKinds();
        var jf = Path.Combine(j.Context.SessionDir, "journal.json");
        var node = JsonNode.Parse(File.ReadAllText(jf))!;
        node["Created"] = DateTimeOffset.Now.AddDays(-2).ToString("o");
        File.WriteAllText(jf, node.ToJsonString());

        var freed = new AblageJanitor(store).Purge(TimeSpan.FromDays(14));

        Assert.Equal(2, freed);
        Assert.False(Directory.Exists(cache.After["ablage"]));
        Assert.True(Directory.Exists(folder.After["ablage"]));
        Assert.True(Directory.Exists(temp.After["ablage"]));
        var records = store.List().Single().Records;
        Assert.Equal("true", records.Single(r => r.Kind == "cache").After["purged"]);
        Assert.DoesNotContain(records.Where(r => r.Kind != "cache"), r => r.After.ContainsKey("purged"));

        var p = new AblageJanitor(store).PreviewNow();
        Assert.Equal(1, p.ProgramFolders);
    }

    [Fact]
    public void Fresh_cache_is_not_auto_purged()
    {
        var (store, _, cache, _, _) = SessionWithAllKinds();
        Assert.Equal(0, new AblageJanitor(store).Purge(TimeSpan.FromDays(14)));
        Assert.True(Directory.Exists(cache.After["ablage"]));
    }

    [Fact]
    public void PurgeNow_deletes_folder_kind_and_preview_counts_it()
    {
        var (store, _, _, folder, _) = SessionWithAllKinds();
        var before = new AblageJanitor(store).PreviewNow();
        Assert.Equal(1, before.ProgramFolders);
        Assert.Equal(2 + 8 + 3, before.PurgeableBytes);

        Assert.Equal(13, new AblageJanitor(store).PurgeNow());

        Assert.False(Directory.Exists(folder.After["ablage"]));
        Assert.Equal(0, new AblageJanitor(store).PreviewNow().ProgramFolders);
    }

    // ---- engine / factory ----

    static ChangeRecord Rec(string kind, string moved) =>
        new("id", kind, "t", "d", new() { ["path"] = "t" }, new() { ["moved"] = moved, ["sidecar"] = "s" },
            ChangeStatus.Applied, DateTimeOffset.Now);

    [Fact]
    public void MovedNothing_covers_cache_and_folder()
    {
        Assert.True(OptimizerEngine.MovedNothing(Rec("cache", "0")));
        Assert.True(OptimizerEngine.MovedNothing(Rec("folder", "0")));
        Assert.False(OptimizerEngine.MovedNothing(Rec("folder", "1")));
        Assert.False(OptimizerEngine.IsUndoable(Rec("folder", "0")));
        Assert.True(OptimizerEngine.IsUndoable(Rec("cache", "3")));
    }

    [Fact]
    public void Factory_rehydrates_cache_and_folder()
    {
        var fo = new FakeFileOps();
        var f = ChangeFactory.CreateDefault(new FakeRegistry(), new FakeServiceControl(), new FakeTaskStore(), fo, RuleSet.Default);
        Assert.Equal("cache", f.Rehydrate(Rec("cache", "1")).Kind);
        Assert.Equal("folder", f.Rehydrate(Rec("folder", "1")).Kind);
        var single = Rec("cache", "1") with { Before = new() { ["path"] = "t", ["single"] = "true" } };
        Assert.Equal("cache", f.Rehydrate(single).Kind);
    }

    [Fact]
    public void Finding_group_defaults_to_null()
    {
        var f = new Finding("x", FindingArea.Speicher, "t", "l", [], Rating.Info, false, null);
        Assert.Null(f.Group);
        Assert.Equal("Verwaiste Ordner", FindingGroups.OrphanFolders);
    }
}
