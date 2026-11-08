using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Tests;

public sealed class JournalTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    static ChangeRecord Rec(string id, ChangeStatus s) =>
        new(id, "file", "C:\\x", "Test", new() { ["a"] = "1" }, new() { ["b"] = null }, s, DateTimeOffset.Now);

    [Fact]
    public void Create_makes_unique_folders_with_subdirs()
    {
        var store = new SessionStore(_root);
        var a = store.Create("A");
        var b = store.Create("B");
        Assert.NotEqual(a.Context.SessionDir, b.Context.SessionDir);
        foreach (var j in new[] { a, b })
        {
            Assert.True(Directory.Exists(j.Context.BackupDir));
            Assert.True(Directory.Exists(j.Context.AblageDir));
        }
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Upsert_persists_and_reloads()
    {
        var store = new SessionStore(_root);
        var j = store.Create("Titel");
        j.Upsert(Rec("1", ChangeStatus.Pending));
        j.Upsert(Rec("1", ChangeStatus.Applied));

        var loaded = new SessionStore(_root).List()[0];
        Assert.Equal("Titel", loaded.Title);
        var r = Assert.Single(loaded.Records);
        Assert.Equal(ChangeStatus.Applied, r.Status);
        Assert.Equal("1", r.Before["a"]);
        Assert.Null(r.After["b"]);
    }

    [Fact]
    public void Corrupt_tmp_file_does_not_break_reload()
    {
        var store = new SessionStore(_root);
        var j = store.Create("T");
        j.Upsert(Rec("1", ChangeStatus.Applied));
        File.WriteAllText(Path.Combine(j.Context.SessionDir, "journal.json.tmp"), "{ broken");

        var loaded = store.List()[0];
        Assert.Single(loaded.Records);
        loaded.Upsert(Rec("2", ChangeStatus.Applied)); // overwrites the stale tmp
        Assert.Equal(2, store.List()[0].Records.Count);
    }

    [Fact]
    public void FileLog_is_noop_before_init_and_prunes_old_files()
    {
        FileLog.Info("ignored"); // must not throw
        var dir = Path.Combine(_root, "Logs");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "winwaechter-20200101.log");
        File.WriteAllText(old, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        FileLog.Init(dir, 7);
        FileLog.Info("hallo");
        FileLog.Error("fehler", new InvalidOperationException("boom"));
        Assert.False(File.Exists(old));
        var log = Assert.Single(Directory.GetFiles(dir));
        Assert.Contains("hallo", File.ReadAllText(log));
    }
}
