using System.IO;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class BuildingBlocks06Tests
{
    static SystemAction Long(string lockKey, Func<Task<ActionOutcome>> run) =>
        new("long", "Lang", "Los", null, false, (_, _) => run()) { SkipGate = true, LockKey = lockKey };

    static Dictionary<string, ActivityLock> Locks() => new()
    {
        ["repair"] = new ActivityLock("Eine Reparatur läuft gerade."),
        ["updates"] = new ActivityLock("Es werden gerade Programme aktualisiert."),
    };

    [Fact]
    public async Task Line_overload_of_fake_replays_stdout_lines_in_order()
    {
        var runner = new FakeProcessRunner { Handler = (_, _) => Task.FromResult((0, "a\r\nb\n", "")) };
        var lines = new List<string>();
        var r = await ((IProcessRunner)runner).RunAsync("x.exe", "", lines.Add, CancellationToken.None);
        Assert.Equal(0, r.Exit);
        Assert.Equal(["a", "b"], lines);
    }

    [Fact]
    public void ProcessRunner_splits_carriage_return_progress()
    {
        Assert.Equal(["10%", "20%", "ok"], ProcessRunner.SplitLines("10%\r20%\r\nok\n"));
    }

    [Fact]
    public async Task Skip_gate_action_runs_while_gate_is_held_and_respects_its_lock()
    {
        var gate = new ChangeGate();
        var hold = new TaskCompletionSource();
        var held = gate.RunAsync(this, () => hold.Task);
        var runner = new ActionRunner(new RecordingDialogs(), gate, locks: Locks());

        var release = new TaskCompletionSource<ActionOutcome>();
        var first = runner.RunAsync(this, Long("repair", () => release.Task), null);
        var secondRan = false;
        var second = await runner.RunAsync(this, Long("repair", () => { secondRan = true; return Task.FromResult(new ActionOutcome(true, "")); }), null);

        Assert.False(second!.Success);
        Assert.Equal("Eine Reparatur läuft gerade.", second.Message);
        Assert.False(secondRan);

        release.SetResult(new ActionOutcome(true, "fertig"));
        var r = await first;
        Assert.True(r!.Success);
        Assert.Equal("fertig", r.Message);
        hold.SetResult();
        await held;
    }

    [Fact]
    public async Task Skip_gate_without_lock_key_throws_ArgumentException()
    {
        var a = new SystemAction("x", "X", "Los", null, false, (_, _) => Task.FromResult(new ActionOutcome(true, ""))) { SkipGate = true };
        await Assert.ThrowsAsync<ArgumentException>(() => new ActionRunner(new RecordingDialogs(), new ChangeGate(), locks: Locks()).RunAsync(this, a, null));
    }

    [Fact]
    public async Task Stamped_action_writes_stamp_and_store_survives_corrupt_file()
    {
        var dir = Directory.CreateTempSubdirectory("ww-stamps").FullName;
        try
        {
            var path = Path.Combine(dir, "aktionen.json");
            File.WriteAllText(path, "{nope");
            var store = new ActionStampStore(path);
            Assert.Null(store.Get("x"));

            var a = new SystemAction("x", "X", "Los", null, false, (_, _) => Task.FromResult(new ActionOutcome(true, "ok"))) { Stamp = true };
            await new ActionRunner(new RecordingDialogs(), new ChangeGate(), stamps: store).RunAsync(this, a, null);

            var s = new ActionStampStore(path).Get("x");
            Assert.NotNull(s);
            Assert.True(s!.Success);
            Assert.Equal("ok", s.Summary);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Recent_restore_point_continues_without_asking()
    {
        var dialogs = new RecordingDialogs();
        var ok = await RestorePointGate.EnsureAsync(
            _ => new RestorePointResult(false, "Ein Wiederherstellungspunkt von 09:12 besteht schon.", RecentExists: true), "Test", dialogs);
        Assert.True(ok);
        Assert.Empty(dialogs.Asks);
    }

    [Fact]
    public async Task Failed_restore_point_asks_and_respects_no()
    {
        var dialogs = new RecordingDialogs { AskAnswer = false };
        var ok = await RestorePointGate.EnsureAsync(_ => new RestorePointResult(false, "kaputt"), "Test", dialogs);
        Assert.False(ok);
        Assert.Single(dialogs.Asks);
        Assert.Contains("Trotzdem fortfahren?", dialogs.Asks[0].Message);
    }

    [Fact]
    public void Delete_on_fake_records_path()
    {
        var store = new FakeTaskStore().Add(@"\WinWaechter\X");
        store.Delete(@"\WinWaechter\X");
        store.Delete(@"\WinWaechter\Missing");
        Assert.Equal([@"\WinWaechter\X", @"\WinWaechter\Missing"], store.DeleteCalls);
        Assert.Null(store.Get(@"\WinWaechter\X"));
    }
}
