using System.Security;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class ServiceTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-services-" + Guid.NewGuid().ToString("N"));
    readonly FakeServiceControl _sc = new();
    readonly FakeSignatures _sig = new();
    readonly FakeRegistry _reg = new();

    public ServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    ServiceScanner Scanner() => new(_sc, _sig, RuleSet.Default, _reg);
    SessionContext Ctx() => new(Path.Combine(_root, "session"));
    ServiceStartModeChange Change(string name = "Glary") => new(_sc, _reg, name);

    [Fact]
    public void Microsoft_signed_service_not_listed()
    {
        _sc.Add("Foo", imagePath: @"C:\Program Files\Foo\foo.exe");
        _sig.Microsoft(@"C:\Program Files\Foo\foo.exe");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Service_under_windows_dir_not_listed()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var exe = win + @"\System32\odd.exe";
        _sc.Add("Odd", imagePath: exe);
        _sig.Unsigned(exe);
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Non_automatic_service_not_listed()
    {
        _sc.Add("Manu", mode: StartMode.Manual, imagePath: @"C:\Apps\m.exe");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Unknown_signature_is_info_not_preselected()
    {
        _sc.Add("Foo", imagePath: @"C:\Apps\foo.exe", displayName: "Foo Dienst");
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Equal("service|Foo", f.Id);
        Assert.Equal(FindingArea.Dienst, f.Area);
        Assert.Equal("Foo Dienst", f.Title);
        Assert.Contains(f.Reasons, r => r.Contains("Startet automatisch"));
        Assert.Contains(f.Reasons, r => r.Contains("nicht prüfbar"));
    }

    [Fact]
    public void Unsigned_unknown_service_outside_program_files_gets_odd_location_reason()
    {
        _sc.Add("Foo", imagePath: @"C:\Users\x\AppData\foo.exe");
        _sig.Unsigned(@"C:\Users\x\AppData\foo.exe");
        var f = Assert.Single(Scanner().Scan());
        Assert.False(f.Preselected);
        Assert.Contains(f.Reasons, r => r.Contains("unsigniert, ungewöhnlicher Ort"));
    }

    [Fact]
    public void Signed_third_party_service_names_publisher()
    {
        _sc.Add("Foo", imagePath: @"C:\Program Files\Foo\foo.exe");
        _sig.Signed(@"C:\Program Files\Foo\foo.exe", "Foo GmbH");
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("Foo GmbH", f.Publisher);
        Assert.Contains(f.Reasons, r => r.Contains("nicht von Microsoft signiert (Foo GmbH)"));
    }

    Finding ScanOne(string name, string display, string exe, SignatureInfo? sig)
    {
        _sc.Add(name, imagePath: exe, displayName: display);
        if (sig is not null) _sig.Set(exe, sig);
        return Assert.Single(Scanner().Scan());
    }

    static SignatureInfo Valid(string publisher) => new(SignatureState.Valid, publisher, false);

    const string SteamExe = @"C:\Program Files (x86)\Steam\bin\steamservice.exe";
    const string GlaryExe = @"C:\Program Files\Glary Utilities\gu.exe";

    [Fact]
    public void Expendable_signed_service_is_entbehrlich_and_preselected()
    {
        var f = ScanOne("Steam Client Service", "Steam Client Service", SteamExe, Valid("Valve"));
        Assert.Equal(Rating.Entbehrlich, f.Rating);
        Assert.True(f.Preselected);
        Assert.NotNull(f.Change);
    }

    [Fact]
    public void Pua_signed_service_is_unerwuenscht_and_preselected()
    {
        var f = ScanOne("GlaryUtilsSvc", "Glary Utilities Service", GlaryExe, Valid("Glarysoft Ltd"));
        Assert.Equal(Rating.Unerwuenscht, f.Rating);
        Assert.True(f.Preselected);
    }

    [Fact]
    public void Miner_service_is_verdaechtig_and_not_preselected()
    {
        var f = ScanOne("xmrigsvc", "xmrig", @"C:\ProgramData\miner\xmrig.exe", Valid("Someone"));
        Assert.Equal(Rating.Verdaechtig, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public void Signed_unknown_third_party_service_is_info_not_preselected()
    {
        var f = ScanOne("Foo", "Foo Dienst", @"C:\Program Files\Foo\foo.exe", Valid("Foo GmbH"));
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
    }

    [Fact]
    public void Expendable_with_unknown_signature_is_downgraded_with_reason()
    {
        var f = ScanOne("Steam Client Service", "Steam Client Service", SteamExe, null);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Contains(f.Reasons, r => r.Contains("bekannt als entbehrlich"));
    }

    [Fact]
    public void Pua_with_unknown_signature_is_downgraded_with_reason()
    {
        var f = ScanOne("GlaryUtilsSvc", "Glary Utilities Service", GlaryExe, null);
        Assert.Equal(Rating.Info, f.Rating);
        Assert.False(f.Preselected);
        Assert.Contains(f.Reasons, r => r.Contains("bekannt als unerwünscht"));
    }

    [Fact]
    public void Protected_service_not_listed()
    {
        _sc.Add("DellUpdate", imagePath: @"C:\Program Files\Dell\du.exe");
        _sig.Signed(@"C:\Program Files\Dell\du.exe", "Dell Inc");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Svchost_hosted_dll_outside_windows_is_checked_by_dll()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _sc.Add("Hosted", imagePath: win + @"\System32\svchost.exe -k netsvcs", serviceDll: @"C:\Apps\hosted.dll");
        _sig.Unsigned(@"C:\Apps\hosted.dll");
        var f = Assert.Single(Scanner().Scan());
        Assert.Equal("service|Hosted", f.Id);
        Assert.Contains(_sig.VerifyCalls, c => c.EndsWith("hosted.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Svchost_hosted_dll_under_windows_not_listed()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _sc.Add("Hosted", imagePath: win + @"\System32\svchost.exe -k netsvcs", serviceDll: win + @"\System32\x.dll");
        Assert.Empty(Scanner().Scan());
    }

    [Fact]
    public void Apply_sets_manual_and_undo_restores_delayed_automatic()
    {
        _sc.Add("Glary", delayed: true, imagePath: @"C:\Apps\g.exe");
        _reg.Seed(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Glary", "Start", 2);
        var ch = Change();

        var rec = ch.Apply(Ctx());

        Assert.Equal("service", rec.Kind);
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal(StartMode.Manual, _sc.Get("Glary")!.Mode);
        Assert.False(_sc.Get("Glary")!.DelayedStart);
        Assert.Equal("Automatic", rec.Before["mode"]);
        Assert.Equal("True", rec.Before["delayed"], ignoreCase: true);
        Assert.Equal("Manual", rec.After["mode"]);
        Assert.True(File.Exists(rec.Before["backup"]));
        Assert.EndsWith("service_Glary.reg", rec.Before["backup"]);

        var again = ServiceStartModeChange.FromRecord(rec, _sc, _reg);
        again.Undo(rec, Ctx());

        var live = _sc.Get("Glary")!;
        Assert.Equal(StartMode.Automatic, live.Mode);
        Assert.True(live.DelayedStart);
    }

    [Fact]
    public void Apply_without_service_key_skips_backup()
    {
        _sc.Add("Glary");
        var rec = Change().Apply(Ctx());
        Assert.Null(rec.Before["backup"]);
        Assert.Empty(_reg.ExportCalls);
    }

    [Fact]
    public void Description_is_german()
    {
        _sc.Add("Glary", displayName: "Glary Utilities");
        Assert.Equal("Dienst „Glary Utilities“ auf Manuell stellen", Change().Description);
    }

    [Fact]
    public void Description_falls_back_to_name_on_win32_error()
    {
        _sc.Add("Glary", displayName: "Glary Utilities");
        _sc.GetError = new System.ComponentModel.Win32Exception(1060);
        Assert.Equal("Dienst „Glary“ auf Manuell stellen", Change().Description);
    }

    [Fact]
    public void Drift_when_mode_changed_externally()
    {
        _sc.Add("Glary");
        var ch = Change();
        var rec = ch.Apply(Ctx());
        Assert.Equal(DriftState.Unchanged, ch.CheckDrift(rec));

        _sc.SetStartMode("Glary", StartMode.Automatic, false);
        Assert.Equal(DriftState.Changed, ch.CheckDrift(rec));

        _sc.Remove("Glary");
        Assert.Equal(DriftState.Gone, ch.CheckDrift(rec));
    }

    sealed class ThrowingControl : IServiceControl
    {
        public IReadOnlyList<ServiceInfo> List() => [];
        public ServiceInfo? Get(string name) => throw new SecurityException("kein Zugriff");
        public void SetStartMode(string name, StartMode mode, bool delayed) { }
    }

    [Fact]
    public void Unreadable_service_is_error_not_gone()
    {
        var ch = new ServiceStartModeChange(new ThrowingControl(), _reg, "X");
        var rec = new ChangeRecord("1", "service", "X", "d",
            new() { ["mode"] = "Automatic", ["delayed"] = "False" }, new() { ["mode"] = "Manual", ["delayed"] = "False" },
            ChangeStatus.Applied, DateTimeOffset.Now);
        Assert.Throws<InvalidOperationException>(() => ch.CheckDrift(rec));
        Assert.Throws<InvalidOperationException>(() => ch.Undo(rec, Ctx()));
    }

    sealed class FailingSet(IServiceControl inner) : IServiceControl
    {
        public IReadOnlyList<ServiceInfo> List() => inner.List();
        public ServiceInfo? Get(string name) => inner.Get(name);
        public void SetStartMode(string name, StartMode mode, bool delayed) =>
            throw new InvalidOperationException("Verzögerter Start fehlgeschlagen");
    }

    [Fact]
    public void Undo_failure_propagates()
    {
        _sc.Add("Glary", delayed: true);
        var rec = Change().Apply(Ctx());
        var ch = new ServiceStartModeChange(new FailingSet(_sc), _reg, "Glary");
        Assert.Throws<InvalidOperationException>(() => ch.Undo(rec, Ctx()));
    }
}
