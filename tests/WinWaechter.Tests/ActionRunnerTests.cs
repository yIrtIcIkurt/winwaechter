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
    public async Task Defender_action_waits_while_Defender_is_busy_elsewhere()
    {
        var dialogs = new RecordingDialogs();
        var activity = new DefenderActivity { Others = () => true };   // e.g. a scan on the Schutz page
        var ran = false;
        var act = Make(() => { ran = true; return Task.FromResult(new ActionOutcome(true, "ok")); }, "Sicher?") with { UsesDefender = true };

        var r = await new ActionRunner(dialogs, new ChangeGate(), activity).RunAsync(this, act, null);

        Assert.False(ran);
        Assert.Empty(dialogs.Confirms);   // refused before the confirmation
        Assert.False(r!.Success);
        Assert.Equal(DefenderActivity.BusyText, r.Message);

        // other actions do not care
        var plain = await new ActionRunner(dialogs, new ChangeGate(), activity).RunAsync(this, act with { UsesDefender = false }, null);
        Assert.True(plain!.Success);
    }

    [Fact]
    public async Task Running_Defender_action_marks_Defender_busy_until_it_ends()
    {
        var activity = new DefenderActivity();
        var release = new TaskCompletionSource();
        var seen = new List<bool>();
        activity.PropertyChanged += (_, _) => seen.Add(activity.IsRunning);
        var act = Make(async () => { await release.Task; return new ActionOutcome(true, "ok"); }) with { UsesDefender = true };

        var running = new ActionRunner(new RecordingDialogs(), new ChangeGate(), activity).RunAsync(this, act, null);
        Assert.True(activity.IsRunning);
        Assert.True(activity.IsBusy);
        Assert.False(activity.TryBegin());   // a second Defender job is refused meanwhile
        release.SetResult();
        await running;

        Assert.False(activity.IsRunning);
        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void Defender_actions_of_the_security_rows_are_marked()
    {
        var set = WinWaechter.Core.Security.Checks.SecurityActionSet.Create(new FakeDefender(), new FakeUpdateInstaller(), new FakeRestarter());
        Assert.True(set.EnableRealTime.UsesDefender);
        Assert.True(set.UpdateSignatures.UsesDefender);
        Assert.False(set.InstallUpdates.UsesDefender);
        Assert.False(set.Restart.UsesDefender);
    }

    [Fact]
    public void Final_without_confirm_text_throws() =>
        Assert.Throws<ArgumentException>(() => Make(() => Task.FromResult(new ActionOutcome(true, "ok")), null, true));
}
