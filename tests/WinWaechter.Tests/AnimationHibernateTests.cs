using Microsoft.Win32;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class AnimationHibernateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-anim-" + Guid.NewGuid().ToString("N"));
    const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public AnimationHibernateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    SessionContext Ctx() => new(Path.Combine(_root, Guid.NewGuid().ToString("N")));

    [Fact]
    public void Animation_apply_undo_and_drift()
    {
        var s = new FakeAnimationSetting { Value = true };
        var c = new AnimationChange(s, false);
        Assert.Equal("spi", c.Kind);
        Assert.Equal("Animationseffekte", c.Target);
        var rec = c.Apply(Ctx());
        Assert.False(s.Value);
        Assert.Equal("true", rec.Before["on"]);
        Assert.Equal("false", rec.After["on"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        s.Value = true;
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
        s.Value = false;
        c.Undo(rec, Ctx());
        Assert.True(s.Value);
    }

    [Fact]
    public void Hibernate_apply_undo_and_drift()
    {
        var h = new FakeHibernation { IsEnabled = true, FileBytes = 13L * 1024 * 1024 * 1024 + 512L * 1024 * 1024 };
        var c = new HibernateChange(h, false);
        Assert.Equal("hibernate", c.Kind);
        Assert.Equal("Ruhezustand", c.Target);
        Assert.Equal("Ruhezustand ausschalten (gibt 13,5 GB auf C: frei)", c.Description);
        var rec = c.Apply(Ctx());
        Assert.False(h.IsEnabled);
        Assert.Equal("true", rec.Before["on"]);
        Assert.Equal(h.FileBytes.ToString(), rec.Before["bytes"]);
        Assert.Equal("false", rec.After["on"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));
        h.IsEnabled = true;
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));
        h.IsEnabled = false;
        c.Undo(rec, Ctx());
        Assert.True(h.IsEnabled);
        Assert.Equal("Ruhezustand einschalten", new HibernateChange(h, true).Description);
        h.Drive = "D:";
        Assert.Equal("Ruhezustand ausschalten (gibt 13,5 GB auf D: frei)", new HibernateChange(h, false).Description);
    }

    [Fact]
    public void Hibernate_uses_only_fixed_powercfg_arguments()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, PowerKey, "HibernateEnabled", 1, RegistryValueKind.DWord);
        var runner = new FakeProcessRunner();
        var h = new WinHibernation(reg, runner, @"C:\Win", _root);
        Assert.True(h.IsEnabled);

        var ex = Assert.Throws<InvalidOperationException>(() => h.Set(false));
        Assert.Equal("powercfg hat den Ruhezustand nicht geändert (Rückgabewert 0).", ex.Message);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Path.Combine(@"C:\Win", "System32", "powercfg.exe"), call.Exe);
        Assert.Equal("/hibernate off", call.Args);

        runner.Handler = (_, _) =>
        {
            reg.Seed(Hive.LocalMachine, PowerKey, "HibernateEnabled", 0, RegistryValueKind.DWord);
            return Task.FromResult((0, "", ""));
        };
        h.Set(false);
        Assert.False(h.IsEnabled);
        Assert.Equal("/hibernate off", runner.Calls[^1].Args);

        runner.Handler = (_, _) => Task.FromResult((1, "", "x"));
        var ex2 = Assert.Throws<InvalidOperationException>(() => h.Set(true));
        Assert.Contains("Rückgabewert 1", ex2.Message);
        Assert.Equal("/hibernate on", runner.Calls[^1].Args);
    }

    [Fact]
    public void Hibernate_enabled_falls_back_to_default_value()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, PowerKey, "HibernateEnabledDefault", 1, RegistryValueKind.DWord);
        var h = new WinHibernation(reg, new FakeProcessRunner(), @"C:\Win", _root);
        Assert.True(h.IsEnabled);
        reg.Seed(Hive.LocalMachine, PowerKey, "HibernateEnabled", 0, RegistryValueKind.DWord);
        Assert.False(h.IsEnabled); // the real value wins
    }

    [Fact]
    public void Powercfg_timeout_gives_german_message()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => Task.FromCanceled<(int, string, string)>(new CancellationToken(true)),
        };
        var h = new WinHibernation(new FakeRegistry(), runner, @"C:\Win", _root);
        var ex = Assert.Throws<InvalidOperationException>(() => h.Set(false));
        Assert.Equal("powercfg hat nicht rechtzeitig geantwortet.", ex.Message);
    }

    [Fact]
    public void Hibernation_file_size_reads_hiberfil_or_zero()
    {
        var h = new WinHibernation(new FakeRegistry(), new FakeProcessRunner(), @"C:\Win", _root);
        Assert.False(h.IsEnabled);
        Assert.Equal(0, h.FileBytes);
        File.WriteAllBytes(Path.Combine(_root, "hiberfil.sys"), new byte[1234]);
        Assert.Equal(1234, h.FileBytes);
    }

    [Fact]
    public void Hibernation_drive_is_the_system_drive_without_backslash()
    {
        Assert.Equal("C:", new WinHibernation(new FakeRegistry(), new FakeProcessRunner(), @"C:\Win", @"C:\").Drive);
        Assert.Equal("D:", new WinHibernation(new FakeRegistry(), new FakeProcessRunner(), @"D:\Win", "D:").Drive);
    }

    [Fact]
    public void FromRecord_rejects_bad_values()
    {
        var s = new FakeAnimationSetting();
        var h = new FakeHibernation();
        var rec = new AnimationChange(s, false).Apply(Ctx());
        Assert.Equal("Animationseffekte", AnimationChange.FromRecord(rec, s).Target);
        var bad = rec with { After = new Dictionary<string, string?> { ["on"] = "vielleicht" } };
        Assert.Throws<InvalidOperationException>(() => AnimationChange.FromRecord(bad, s));

        var hrec = new HibernateChange(h, false).Apply(Ctx());
        Assert.Equal("Ruhezustand", HibernateChange.FromRecord(hrec, h).Target);
        var hbad = hrec with { After = new Dictionary<string, string?> { ["on"] = "vielleicht" } };
        Assert.Throws<InvalidOperationException>(() => HibernateChange.FromRecord(hbad, h));

        var badBefore = rec with { Before = new Dictionary<string, string?> { ["on"] = "x" } };
        s.SetCalls.Clear();
        Assert.Throws<InvalidOperationException>(() => AnimationChange.FromRecord(badBefore, s).Undo(badBefore, Ctx()));
        Assert.Empty(s.SetCalls);
    }
}
