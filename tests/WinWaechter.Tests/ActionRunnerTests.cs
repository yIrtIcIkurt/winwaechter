using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class ActionRunnerTests
{
    static SystemAction Make(Func<Task<ActionOutcome>> run, string? confirm = null, bool final = false, IReadOnlyList<string>? items = null) =>
        new("test", "Testaktion", "Los", confirm, final, (_, _) => run(), items);

    [Fact]
    public async Task Cancellation_is_no_error_dialog()
    {
        var dialogs = new RecordingDialogs();
        var r = await new ActionRunner(dialogs, new ChangeGate()).RunAsync(this,
            Make(() => throw new OperationCanceledException()), null);
        Assert.False(r!.Success);
        Assert.Equal("Abgebrochen.", r.Message);
        Assert.Empty(dialogs.Informs);
    }

    [Fact]
    public async Task Final_action_without_confirm_text_is_refused()
    {
        var dialogs = new RecordingDialogs();
        var ran = false;
        var bad = Make(() => { ran = true; return Task.FromResult(new ActionOutcome(true, "ok")); }, "x", true) with { ConfirmText = null };
        await Assert.ThrowsAsync<ArgumentException>(() => new ActionRunner(dialogs, new ChangeGate()).RunAsync(this, bad, null));
        Assert.False(ran);
        Assert.Empty(dialogs.Confirms);
    }

    [Fact]
    public async Task Harmless_action_runs_without_confirm()
    {
        var dialogs = new RecordingDialogs();
        var ran = false;
        var r = await new ActionRunner(dialogs, new ChangeGate()).RunAsync(this,
            Make(() => { ran = true; return Task.FromResult(new ActionOutcome(true, "ok")); }), null);
        Assert.True(ran);
        Assert.Empty(dialogs.Confirms);
        Assert.True(r!.Success);
    }

    [Fact]
    public async Task Final_action_confirm_says_endgueltig_and_final_flag()
    {
        var dialogs = new RecordingDialogs();
        var items = new[] { "a", "b" };
        await new ActionRunner(dialogs, new ChangeGate()).RunAsync(this,
            Make(() => Task.FromResult(new ActionOutcome(true, "ok")), "Wirklich?", true, items), null);
        var c = Assert.Single(dialogs.Confirms);
        Assert.EndsWith(ActionRunner.FinalNote, c.Message);
        Assert.StartsWith("Wirklich?", c.Message);
        Assert.True(c.Final);
        Assert.Equal("Los", c.Button);
        Assert.Equal(items, c.Items);
    }

    [Fact]
    public async Task Declined_runs_nothing()
    {
        var dialogs = new RecordingDialogs { ConfirmAnswer = false };
        var ran = false;
        var r = await new ActionRunner(dialogs, new ChangeGate()).RunAsync(this,
            Make(() => { ran = true; return Task.FromResult(new ActionOutcome(true, "ok")); }, "Sicher?"), null);
        Assert.Null(r);
        Assert.False(ran);
    }

    [Fact]
    public async Task Busy_gate_returns_busy_hint()
    {
        var gate = new ChangeGate();
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var holder = gate.RunAsync(new object(), async () => { entered.SetResult(); await release.Task; });
        await entered.Task;
        var ran = false;
        var r = await new ActionRunner(new RecordingDialogs(), gate).RunAsync(this,
            Make(() => { ran = true; return Task.FromResult(new ActionOutcome(true, "ok")); }), null);
        release.SetResult();
        await holder;
        Assert.False(ran);
        Assert.False(r!.Success);
        Assert.Equal(ChangeGate.BusyHint, r.Message);
    }

    [Fact]
    public async Task Exception_is_logged_and_reported()
    {
        var dialogs = new RecordingDialogs();
        var r = await new ActionRunner(dialogs, new ChangeGate()).RunAsync(this,
            Make(() => throw new InvalidOperationException("kaputt")), null);
        Assert.False(r!.Success);
        Assert.Equal("kaputt", r.Message);
        var i = Assert.Single(dialogs.Informs);
        Assert.Equal("Testaktion fehlgeschlagen", i.Title);
        Assert.Equal("kaputt", i.Message);
    }

    [Fact]
    public void Final_without_confirm_text_throws() =>
        Assert.Throws<ArgumentException>(() => Make(() => Task.FromResult(new ActionOutcome(true, "ok")), null, true));
}
