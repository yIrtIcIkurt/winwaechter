using System.Text.Json.Nodes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
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
}
