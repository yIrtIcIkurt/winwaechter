using Microsoft.Win32;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class DefenderParsingTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Threat_join_marks_active_by_status()
    {
        var threats = new[]
        {
            new DefenderClient.ThreatRow(1, "Trojan:A", 5, true),
            new DefenderClient.ThreatRow(2, "PUA:B", 2, false),
            new DefenderClient.ThreatRow(3, "Nothing", 1, false),
        };
        var detections = new[]
        {
            new DefenderClient.DetectionRow(1, 2, T0, ["C:\\a.exe"]),                           // older: cleaned
            new DefenderClient.DetectionRow(1, 1, T0.AddHours(1), ["C:\\a.exe", "C:\\b.exe"]),  // latest: detected
            new DefenderClient.DetectionRow(2, 3, T0, ["C:\\p.exe"]),
        };

        var r = DefenderClient.JoinThreats(threats, detections);

        Assert.Equal(3, r.Count);
        var a = r.Single(t => t.ThreatId == 1);
        Assert.True(a.Active);
        Assert.Equal("Erkannt", a.StatusText);
        Assert.Equal(["C:\\a.exe", "C:\\b.exe"], a.Resources);
        var b = r.Single(t => t.ThreatId == 2);
        Assert.False(b.Active);
        Assert.Equal("In Quarantäne", b.StatusText);
        var c = r.Single(t => t.ThreatId == 3);
        Assert.Equal("Unbekannt", c.StatusText);
        Assert.Empty(c.Resources);
    }

    [Theory]
    [InlineData(0, "Unbekannt")] [InlineData(1, "Erkannt")] [InlineData(2, "Bereinigt")]
    [InlineData(3, "In Quarantäne")] [InlineData(4, "Entfernt")] [InlineData(5, "Zugelassen")]
    [InlineData(6, "Blockiert")] [InlineData(102, "Quarantäne fehlgeschlagen")]
    [InlineData(103, "Entfernen fehlgeschlagen")] [InlineData(104, "Zulassen fehlgeschlagen")]
    [InlineData(105, "Abgebrochen")] [InlineData(107, "Blockieren fehlgeschlagen")]
    public void Status_text_map(int id, string text)
    {
        Assert.Equal(text, DefenderClient.StatusText(id, false));
        Assert.Equal(text, DefenderClient.StatusText(id, true));
    }

    [Fact]
    public void Status_text_unknown_code_depends_on_active()
    {
        Assert.Equal("Abgeschlossen (Code 106)", DefenderClient.StatusText(106, false));
        Assert.Equal("Unbekannter Status (Code 106)", DefenderClient.StatusText(106, true));
        Assert.Equal("In Quarantäne", DefenderClient.StatusText(3, true));
    }

    [Fact]
    public void MpCmdRun_args()
    {
        Assert.Equal("-Scan -ScanType 1", MpCmdRunArgs.Scan(ScanKind.Quick));
        Assert.Equal("-Scan -ScanType 2", MpCmdRunArgs.Scan(ScanKind.Full));
        Assert.Equal("-Scan -Cancel", MpCmdRunArgs.Cancel);
        Assert.Equal("-SignatureUpdate", MpCmdRunArgs.Update);
        Assert.Equal("-Scan -ScanType 3 -File \"C:\\x y\\f.exe\" -DisableRemediation", MpCmdRunArgs.ScanPath("C:\\x y\\f.exe"));
    }

    [Fact]
    public async Task Scan_uses_registry_path_and_returns_exit_code()
    {
        var reg = new FakeRegistry().Seed(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender", "InstallLocation", @"C:\Def\");
        var run = new FakeProcessRunner { Handler = (_, _) => Task.FromResult((2, "", "")) };
        var d = new DefenderClient(run, reg, _ => true);

        var exit = await d.ScanAsync(ScanKind.Quick, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Equal((Path.Combine(@"C:\Def\", "MpCmdRun.exe"), "-Scan -ScanType 1"), run.Calls.Single());
    }

    [Fact]
    public async Task Scan_cancel_runs_cancel_then_waits_and_throws()
    {
        var release = new TaskCompletionSource<(int, string, string)>();
        var run = new FakeProcessRunner();
        run.Handler = (_, args) =>
        {
            if (args != MpCmdRunArgs.Cancel) return release.Task;
            release.TrySetResult((1, "", ""));   // cancelling ends the scan process
            return Task.FromResult((0, "", ""));
        };
        var d = new DefenderClient(run, new FakeRegistry());
        using var cts = new CancellationTokenSource();

        var scan = d.ScanAsync(ScanKind.Full, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(2, run.Calls.Count);
        Assert.EndsWith("-Scan -Cancel", run.Calls[1].Args);
    }

    [Fact]
    public async Task Powershell_commands_and_errors()
    {
        var run = new FakeProcessRunner();
        var d = new DefenderClient(run, new FakeRegistry());

        await d.RemoveActiveThreatsAsync();
        await d.SetPuaModeAsync(1);

        Assert.All(run.Calls, c => { Assert.True(Path.IsPathRooted(c.Exe)); Assert.EndsWith("powershell.exe", c.Exe); });
        Assert.Equal("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Remove-MpThreat\"", run.Calls[0].Args);
        Assert.Equal("-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Set-MpPreference -PUAProtection 1\"", run.Calls[1].Args);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => d.SetPuaModeAsync(3));

        run.Handler = (_, _) => Task.FromResult((1, "", "boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.SetPuaModeAsync(2));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void ScanPath_rejects_quotes_and_escapes_trailing_backslash()
    {
        Assert.Throws<ArgumentException>(() => MpCmdRunArgs.ScanPath("C:\\a\" -x"));
        Assert.Throws<ArgumentException>(() => MpCmdRunArgs.ScanPath("C:\\a\nb"));
        Assert.Equal("-Scan -ScanType 3 -File \"C:\\dir\\\\\" -DisableRemediation", MpCmdRunArgs.ScanPath("C:\\dir\\"));
    }

    [Fact]
    public async Task Mp_path_falls_back_when_registry_value_invalid()
    {
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        foreach (var (loc, exists) in new[] { (@"C:\Def\", false), (@"relative", true) })
        {
            var reg = new FakeRegistry().Seed(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender", "InstallLocation", loc);
            var run = new FakeProcessRunner();
            await new DefenderClient(run, reg, _ => exists).UpdateSignaturesAsync(CancellationToken.None);
            Assert.Equal(fallback, run.Calls.Single().Exe);
        }
    }

    [Fact]
    public async Task Scan_already_cancelled_does_not_start()
    {
        var run = new FakeProcessRunner();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DefenderClient(run, new FakeRegistry()).ScanAsync(ScanKind.Quick, cts.Token));
        Assert.Empty(run.Calls);
    }

    // Read-only: WMI queries only.
    [Fact]
    public async Task Status_and_threats_smoke_read_only()
    {
        var d = new DefenderClient(new ProcessRunner(), new FakeRegistry());
        var s = await d.GetStatusAsync();
        if (!s.Available) Assert.NotNull(s.Note);
        Assert.NotNull(await d.GetThreatsAsync());
    }

    static SessionContext Ctx() => new(Path.Combine(Path.GetTempPath(), "ww-unused"));

    [Fact]
    public void Pua_change_undo_restores_mode()
    {
        var d = new FakeDefender { PuaMode = 0 };
        var c = new PuaProtectionChange(d);

        var rec = c.Apply(Ctx());
        Assert.Equal(1, d.PuaMode);
        Assert.Equal("pua", rec.Kind);
        Assert.Equal("0", rec.Before["mode"]);
        Assert.Equal("1", rec.After["mode"]);
        Assert.Equal(DriftState.Unchanged, c.CheckDrift(rec));

        d.PuaMode = 2;
        Assert.Equal(DriftState.Changed, c.CheckDrift(rec));

        PuaProtectionChange.FromRecord(rec, d).Undo(rec, Ctx());
        Assert.Equal(0, d.PuaMode);
    }

    [Fact]
    public void Pua_apply_fails_when_defender_unavailable()
    {
        var d = new UnavailableDefender();
        Assert.Throws<InvalidOperationException>(() => new PuaProtectionChange(d).Apply(Ctx()));
        Assert.Empty(d.SetPuaCalls);
    }

    sealed class UnavailableDefender : IDefender
    {
        public List<int> SetPuaCalls { get; } = [];
        public Task<DefenderStatus> GetStatusAsync() => Task.FromResult(new DefenderStatus(false, false, false, 0, null, 0, null, null, "x"));
        public Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync() => throw new NotSupportedException();
        public Task<int> UpdateSignaturesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ScanAsync(ScanKind kind, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ScanPathAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> RemoveActiveThreatsAsync() => throw new NotSupportedException();
        public Task SetPuaModeAsync(int mode) { SetPuaCalls.Add(mode); return Task.CompletedTask; }
    }

    [Fact]
    public void Registration_registers_pua()
    {
        var f = new ChangeFactory();
        var d = new FakeDefender();
        DefenderRegistration.Register(f, d);
        var rec = new PuaProtectionChange(d).Apply(Ctx());
        Assert.IsType<PuaProtectionChange>(f.Rehydrate(rec));
    }
}
