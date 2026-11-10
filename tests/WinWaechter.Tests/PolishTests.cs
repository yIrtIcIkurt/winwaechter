using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.Tests;

/// <summary>Task 10: undo progress, rules in the settings, about block, author metadata, rules wiring.</summary>
public sealed class PolishTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-polish-" + Guid.NewGuid().ToString("N"));
    readonly ChangeGate _gate = new();

    public PolishTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------- undo progress ----------

    sealed class FakeChange(string target) : IChange
    {
        public string Kind => "fake";
        public string Target => target;
        public string Description => "Fake " + target;
        public int Undos { get; private set; }

        public ChangeRecord Apply(SessionContext ctx) =>
            new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new() { ["x"] = "1" }, ChangeStatus.Applied, DateTimeOffset.Now);

        public void Undo(ChangeRecord record, SessionContext ctx) => Undos++;
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    // reports inline, in order (Progress<T> would post to the pool)
    sealed class Collect<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) { lock (Items) Items.Add(value); }
    }

    sealed class Dialogs : IDialogService
    {
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) => true;
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) { }
    }

    string TempWith(int files)
    {
        var dir = Path.Combine(_root, "temp");
        Directory.CreateDirectory(dir);
        var old = DateTime.UtcNow.AddDays(-5);
        for (var i = 0; i < files; i++)
        {
            var p = Path.Combine(dir, $"f{i:D4}.tmp");
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, old);
        }
        return dir;
    }

    (OptimizerEngine Engine, SessionStore Store, MoveToAblageChange Temp, FakeChange Fake) Setup(int files)
    {
        var ops = new WinFileOps();
        var dir = TempWith(files);
        var store = new SessionStore(Path.Combine(_root, "pd"));
        var fake = new FakeChange("x");
        var factory = new ChangeFactory();
        factory.Register("temp", r => MoveToAblageChange.FromRecord(r, ops));
        factory.Register("fake", _ => fake);
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"));
        return (engine, store, new MoveToAblageChange(ops, dir, MoveToAblageChange.DefaultMinAge), fake);
    }

    static Finding F(IChange c) => new(c.Target, FindingArea.Speicher, c.Target, c.Target, ["r"], Rating.Entbehrlich, true, c);

    [Fact]
    public async Task Undo_reports_per_record_and_every_500_files()
    {
        var (engine, store, temp, fake) = Setup(1200);
        // applied fake first, temp second: undo runs temp (1/2), then fake (2/2)
        var applied = await engine.ApplyAsync("T", [F(fake), F(temp)], null, CancellationToken.None);
        Assert.Equal(2, applied.Applied);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "temp")));

        var progress = new Collect<UndoProgress>();
        var res = engine.UndoSession(store.List().Single(), progress);

        Assert.Equal(2, res.Undone);
        Assert.Equal(1200, Directory.EnumerateFiles(Path.Combine(_root, "temp")).Count());
        var texts = progress.Items.Select(p => p.Text).ToList();
        Assert.Equal(
        [
            $"(1/2) {temp.Description}",
            $"(1/2) {temp.Description} – 500 Dateien zurückgeholt",
            $"(1/2) {temp.Description} – 1000 Dateien zurückgeholt",
            "(2/2) Fake x",
        ], texts);
        Assert.All(progress.Items, p => Assert.Equal(2, p.Count));
        Assert.Equal([1, 1, 1, 2], progress.Items.Select(p => p.Index));
        Assert.Equal(500, OptimizerEngine.FileProgressStep);
    }

    [Fact]
    public async Task Undo_without_progress_still_works()
    {
        var (engine, store, temp, fake) = Setup(3);
        await engine.ApplyAsync("T", [F(fake), F(temp)], null, CancellationToken.None);

        var res = engine.UndoSession(store.List().Single());

        Assert.Equal((2, 0, 0), (res.Undone, res.SkippedDrift, res.Failed));
        Assert.Empty(res.Messages);
        Assert.Equal(1, fake.Undos);
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(_root, "temp")).Count());
        Assert.All(store.List().Single().Records, r => Assert.Equal(ChangeStatus.Undone, r.Status));
    }

    // Undo blocks until the test releases it.
    sealed class GatedChange(ManualResetEventSlim release) : IChange
    {
        public string Kind => "gated";
        public string Target => "g";
        public string Description => "Gesperrte Änderung";
        public ChangeRecord Apply(SessionContext ctx) => throw new NotSupportedException();
        public void Undo(ChangeRecord record, SessionContext ctx) => release.Wait(TimeSpan.FromSeconds(10));
        public DriftState CheckDrift(ChangeRecord record) => DriftState.Unchanged;
    }

    [Fact]
    public async Task History_shows_progress_while_undoing()
    {
        using var release = new ManualResetEventSlim();
        var store = new SessionStore(Path.Combine(_root, "pd"));
        store.Create("T").Upsert(new ChangeRecord("1", "gated", "g", "Gesperrte Änderung", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now));
        var factory = new ChangeFactory();
        factory.Register("gated", _ => new GatedChange(release));
        var engine = new OptimizerEngine([], store, factory, _ => new RestorePointResult(true, "ok"));
        var h = new HistoryViewModel(store, engine, new AblageJanitor(store), new Dialogs(), true, _gate);
        await h.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("", h.ProgressText);

        var undo = h.UndoOneCommand.ExecuteAsync(h.Sessions.Single().Records.Single());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (h.ProgressText == "" && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal("(1/1) Gesperrte Änderung", h.ProgressText);
        Assert.Equal(0, h.ProgressValue);
        Assert.True(h.IsBusy);

        release.Set();
        await undo;
        Assert.Equal("", h.ProgressText);
        Assert.Equal(0, h.ProgressValue);
        Assert.False(h.IsBusy);
        Assert.Equal(ChangeStatus.Undone, store.List().Single().Records.Single().Status);
    }

    // ---------- settings: rules and about ----------

    sealed class RecordingShell : ISystemShell
    {
        public List<string> Links { get; } = [];
        public void OpenFolder(string path) { }
        public void OpenLink(string uri) => Links.Add(uri);
    }

    sealed class NoUpdates : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
        public void Launch(VerifiedUpdate update) { }
    }

    SettingsViewModel Settings(RulesInfo info, Func<CancellationToken, Task<RulesUpdateResult>> update, ISystemShell shell,
        Action<string>? copyText = null)
    {
        var env = new AppEnvironment(false, _root, new SemVer(0, 3, 0));
        var dialogs = new Dialogs();
        return new SettingsViewModel(new AppSettings(), env, new UpdateViewModel(new NoUpdates(), dialogs, false, () => { }, _gate),
            shell, info, update, copyText ?? (_ => { }));
    }

    [Fact]
    public async Task Settings_shows_rules_and_updates_them()
    {
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        const string message = "Neue Regeln (Version 3) geladen. Sie gelten ab dem nächsten Start.";
        var calls = 0;
        var vm = Settings(new RulesInfo(2, true, at), _ =>
        {
            calls++;
            return Task.FromResult(new RulesUpdateResult(RulesAcceptance.Accepted, message));
        }, new RecordingShell());

        Assert.StartsWith("Regeln: Version 2 vom ", vm.RulesText);
        Assert.Null(vm.RulesStatus);
        await vm.UpdateRulesCommand.ExecuteAsync(null);
        Assert.Equal(message, vm.RulesStatus);
        Assert.Equal(1, calls);

        var bundled = Settings(new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")), new RecordingShell());
        Assert.Equal("Regeln: Version 1 (mitgeliefert)", bundled.RulesText);
    }

    [Fact]
    public void Settings_about_block_copies_repository_link_without_opening_it()
    {
        var shell = new RecordingShell();
        var copied = new List<string>();
        var vm = Settings(new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")), shell, copied.Add);

        Assert.Equal("Erstellt von Tahsin Yilmaz", SettingsViewModel.AuthorText);
        Assert.Equal("https://github.com/yIrtIcIkurt/winwaechter", SettingsViewModel.RepositoryUrl);
        Assert.Null(vm.LinkCopyStatus);
        vm.CopyRepositoryLinkCommand.Execute(null);
        Assert.Equal(["https://github.com/yIrtIcIkurt/winwaechter"], copied);
        Assert.Equal("Link kopiert", vm.LinkCopyStatus);
        Assert.Empty(shell.Links);
        Assert.False(SystemLinks.IsAllowed(SettingsViewModel.RepositoryUrl));

        // a locked clipboard is reported, never thrown
        var failing = Settings(new RulesInfo(1, false, null), _ => Task.FromResult(new RulesUpdateResult(null, "")), shell,
            _ => throw new System.Runtime.InteropServices.COMException("busy"));
        failing.CopyRepositoryLinkCommand.Execute(null);
        Assert.Equal("Link konnte nicht kopiert werden.", failing.LinkCopyStatus);
    }

    [Fact]
    public void Assembly_metadata_names_author()
    {
        foreach (var asm in new[] { typeof(SettingsViewModel).Assembly, typeof(OptimizerEngine).Assembly })
        {
            Assert.Equal("Tahsin Yilmaz", asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
            Assert.Equal("© 2026 Tahsin Yilmaz", asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
            Assert.Equal("WinWächter", asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        }
    }

    // ---------- rules wiring ----------

    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string PubPem => _key.ExportSubjectPublicKeyInfoPem();

    static byte[] Versioned(int v) =>
        Encoding.UTF8.GetBytes($"{{\"protected\":[{{\"name\":\"Base\"}}],\"rulesVersion\":{v},\"minAppVersion\":\"0.3.0\"}}");

    static string Sign(ECDsa key, byte[] json) =>
        Convert.ToBase64String(key.SignData(RulesSignature.Payload(json), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    sealed class Serve(byte[] json, string sig) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(r.RequestUri == RulesFetcher.JsonUrl ? json : Encoding.ASCII.GetBytes(sig)),
            });
    }

    [Fact]
    public async Task Rules_updater_starts_from_the_loaded_cache_version()
    {
        var dir = Path.Combine(_root, "Regeln");
        var newer = Versioned(RuleSet.Default.RulesVersion + 5);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, RulesStore.JsonName), newer);
        File.WriteAllText(Path.Combine(dir, RulesStore.SigName), Sign(_key, newer), Encoding.ASCII);
        var store = new RulesStore(dir, PubPem, _ => true, _ => { });

        // GitHub serves a signed file that is newer than the bundled set but older than the cache
        var older = Versioned(RuleSet.Default.RulesVersion + 3);
        var (rules, info, update) = AppServices.LoadRules(store, new SemVer(0, 3, 0), new Serve(older, Sign(_key, older)));

        Assert.True(info.FromUpdate);
        Assert.Equal(RuleSet.Default.RulesVersion + 5, info.Version);
        Assert.Equal(info.Version, rules.RulesVersion);
        var r = await update(CancellationToken.None);
        Assert.Equal(RulesAcceptance.NotNewer, r.Acceptance);
        Assert.Equal(newer, File.ReadAllBytes(Path.Combine(dir, RulesStore.JsonName)));
    }

    [Fact]
    public void Rules_folder_is_secured_only_when_elevated()
    {
        foreach (var elevated in new[] { false, true })
        {
            var secured = new List<string>();
            var env = new AppEnvironment(elevated, _root, new SemVer(0, 3, 0));
            var store = AppServices.CreateRulesStore(env, secured.Add);
            Assert.Equal(Path.Combine(_root, "Regeln"), env.RulesDir);
            Assert.Equal(env.RulesDir, store.Dir);
            Assert.Equal(elevated ? new[] { env.RulesDir } : [], secured);
        }
    }

    [Fact]
    public void Rules_signature_needs_p256_and_a_signature()
    {
        var json = Versioned(2);
        Assert.True(RulesSignature.Verify(json, Sign(_key, json), PubPem));
        Assert.False(RulesSignature.Verify(json, null, PubPem));
        Assert.False(RulesSignature.Verify(json, "", PubPem));
        Assert.False(RulesSignature.Verify(json, "   ", PubPem));

        // another 256-bit curve with a valid signature is still refused
        using var brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        Assert.Equal(256, brainpool.KeySize);
        Assert.False(RulesSignature.Verify(json, Sign(brainpool, json), brainpool.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Rules_store_load_never_throws()
    {
        var dir = Path.Combine(_root, "Regeln");
        var json = Versioned(2);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, RulesStore.JsonName), json);
        File.WriteAllText(Path.Combine(dir, RulesStore.SigName), Sign(_key, json), Encoding.ASCII);

        var store = new RulesStore(dir, PubPem, _ => throw new InvalidOperationException("Besitzer nicht lesbar"), _ => { });
        Assert.Null(store.LoadVerified());
        Assert.NotNull(new RulesStore(dir, PubPem, _ => true, _ => { }).LoadVerified());
    }
}
