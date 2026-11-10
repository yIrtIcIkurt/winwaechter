using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;
using WinWaechter.ReleaseTool;

namespace WinWaechter.Tests;

public sealed class RulesUpdateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-rules-" + Guid.NewGuid().ToString("N"));
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly SemVer V030 = new(0, 3, 0);

    public RulesUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    string PubPem => _key.ExportSubjectPublicKeyInfoPem();

    // Minimal valid rules JSON; extraFields are appended after "protected" (no leading comma).
    static byte[] Rules(string extraFields, string protectedJson = "[{\"name\":\"Base\"}]")
        => Encoding.UTF8.GetBytes("{\"protected\":" + protectedJson + (extraFields.Length > 0 ? "," + extraFields : "") + "}");

    static byte[] Versioned(int v, string min = "0.3.0", string protectedJson = "[{\"name\":\"Base\"}]")
        => Rules($"\"rulesVersion\":{v},\"minAppVersion\":\"{min}\"", protectedJson);

    // Signature over the domain prefix + bytes, computed here independently of RulesSignature.
    string Sign(byte[] json)
    {
        var payload = Encoding.UTF8.GetBytes("winwaechter-rules\n").Concat(json).ToArray();
        return Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    string StoreDir => Path.Combine(_root, "Regeln");

    // Version of the bundled rules; it grows with every rules release, so loader tests count from it.
    static int B => RuleSet.Default.RulesVersion;

    RulesStore Store(Func<string, bool>? trusted = null)
        => new(StoreDir, PubPem, trusted ?? (_ => true), _ => { });

    void WriteCache(byte[] json, string sig)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllBytes(Path.Combine(StoreDir, RulesStore.JsonName), json);
        File.WriteAllText(Path.Combine(StoreDir, RulesStore.SigName), sig, Encoding.ASCII);
    }

    // ---- HostPolicy ----

    [Fact]
    public void HostPolicy_instances_have_separate_host_lists()
    {
        var raw = new Uri("https://raw.githubusercontent.com/x");
        Assert.False(HostPolicy.Updates.IsAllowed(raw));
        Assert.True(HostPolicy.Rules.IsAllowed(raw));
        Assert.True(HostPolicy.Rules.IsAllowed(new Uri("https://api.github.com/x")));
        foreach (var bad in new[]
                 {
                     "http://raw.githubusercontent.com/x", "https://u:p@raw.githubusercontent.com/x",
                     "https://raw.githubusercontent.com:8443/x", "https://raw.githubusercontent.com.evil.com/x",
                 })
        {
            Assert.False(HostPolicy.Updates.IsAllowed(new Uri(bad)), bad);
            Assert.False(HostPolicy.Rules.IsAllowed(new Uri(bad)), bad);
        }
    }

    // ---- RuleSet ----

    [Fact]
    public void RuleSet_reads_version_and_min_app()
    {
        Assert.True(RuleSet.Default.RulesVersion >= 1);
        Assert.Equal("0.3.0", RuleSet.Default.MinAppVersion);
        var bare = RuleSet.Parse(Rules(""));
        Assert.Equal(0, bare.RulesVersion);
        Assert.Null(bare.MinAppVersion);
    }

    [Fact]
    public void Union_adds_lists_and_never_removes()
    {
        var remote = RuleSet.Parse(Rules(
            "\"pua\":[{\"name\":\"BadTool\"}],\"minerNames\":[\"xmrnew\"],\"minerCommandLine\":[\"--new-pool\"],"
            + "\"poolPorts\":[12345],\"leftoverIgnore\":[\"RemoteKeep\"],\"rulesVersion\":2,\"minAppVersion\":\"0.3.0\"",
            "[{\"name\":\"Foo Remote\"}]"));
        var u = RuleSet.Default.UnionWith(remote);

        Assert.Equal(RuleVerdict.Protected, u.Classify(new ItemFacts("Foo Remote", null, null, null)));
        Assert.Equal(RuleVerdict.Protected, u.Classify(new ItemFacts("Dell Update", null, null, null)));
        Assert.Equal(RuleVerdict.Pua, u.Classify(new ItemFacts("BadTool", null, null, null)));
        Assert.Contains(3333, u.PoolPorts);
        Assert.Contains(12345, u.PoolPorts);
        Assert.Contains("xmrnew", u.MinerNames);
        Assert.All(RuleSet.Default.MinerNames, n => Assert.Contains(n, u.MinerNames));
        Assert.True(u.IsMinerCommandLine("x --new-pool"));
        Assert.True(u.IsMinerCommandLine("stratum+tcp://a"));
        Assert.True(u.IsLeftoverIgnored("RemoteKeep"));
        Assert.True(u.IsLeftoverIgnored("Common Files"));
        Assert.Equal(2, u.RulesVersion);
        Assert.Equal("0.3.0", u.MinAppVersion);
    }

    [Fact]
    public void Union_never_takes_privacy_from_remote()
    {
        var remote = RuleSet.Parse(Rules(
            "\"privacy\":[{\"id\":\"evil\",\"title\":\"Böse\",\"values\":[{\"hive\":\"HKCU\",\"key\":\"Software\\\\Evil\",\"name\":\"X\",\"target\":1}]}]"));
        Assert.Single(remote.Privacy); // parsed, but must not be taken over
        var u = RuleSet.Default.UnionWith(remote);
        Assert.Equal(RuleSet.Default.Privacy.Select(p => p.Id), u.Privacy.Select(p => p.Id));
        Assert.Null(u.FindPrivacyValue(Hive.CurrentUser, @"Software\Evil", "X"));
    }

    // ---- Signature ----

    [Fact]
    public void Signature_uses_domain_prefix()
    {
        var path = Path.Combine(_root, "rules.json");
        var bytes = Versioned(2);
        File.WriteAllBytes(path, bytes);
        var sigPath = ReleaseSigner.SignRules(_key, path);
        Assert.Equal(path + ".sig", sigPath);
        var sig = File.ReadAllText(sigPath, Encoding.ASCII);
        Assert.True(RulesSignature.Verify(bytes, sig, PubPem));

        var noPrefix = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(RulesSignature.Verify(bytes, noPrefix, PubPem));

        var flipped = (byte[])bytes.Clone();
        flipped[5] ^= 1;
        Assert.False(RulesSignature.Verify(flipped, sig, PubPem));
        Assert.False(RulesSignature.Verify(bytes, "%%%", PubPem));
        Assert.False(RulesSignature.Verify(bytes, Convert.ToBase64String(new byte[63]), PubPem));
        Assert.Equal(Encoding.UTF8.GetBytes("winwaechter-rules\n").Concat(bytes).ToArray(), RulesSignature.Payload(bytes));
    }

    // ---- Store ----

    [Fact]
    public void Store_accepts_only_valid_newer_compatible()
    {
        var store = Store();
        var v2 = Versioned(2);
        Assert.Equal(RulesAcceptance.Accepted, store.TryAccept(v2, Sign(v2), 1, V030));
        Assert.True(File.Exists(Path.Combine(StoreDir, RulesStore.JsonName)));
        Assert.True(File.Exists(Path.Combine(StoreDir, RulesStore.SigName)));

        Assert.Equal(RulesAcceptance.NotNewer, store.TryAccept(v2, Sign(v2), 2, V030));

        var tooNew = Versioned(3, "0.4.0");
        Assert.Equal(RulesAcceptance.AppTooOld, store.TryAccept(tooNew, Sign(tooNew), 1, V030));

        var v3 = Versioned(3);
        Assert.Equal(RulesAcceptance.BadSignature, store.TryAccept(v3, Sign(Versioned(4)), 1, V030));

        var empty = Versioned(3, protectedJson: "[]");
        Assert.Equal(RulesAcceptance.Invalid, store.TryAccept(empty, Sign(empty), 1, V030));

        var noMin = Rules("\"rulesVersion\":3");
        Assert.Equal(RulesAcceptance.Invalid, store.TryAccept(noMin, Sign(noMin), 1, V030));

        Assert.Equal(2, RuleSet.Load(Path.Combine(StoreDir, RulesStore.JsonName)).RulesVersion);
        Assert.Equal(2, store.LoadVerified()!.Rules.RulesVersion);
    }

    // ---- Loader ----

    [Fact]
    public void Load_prefers_higher_valid_cached_version_and_unions()
    {
        var store = Store();
        var newer = Versioned(B + 1, protectedJson: "[{\"name\":\"Foo Remote\"}]");
        Assert.Equal(RulesAcceptance.Accepted, store.TryAccept(newer, Sign(newer), B, V030));

        var (rules, info) = RulesLoader.Load(RuleSet.Default, store, V030);
        Assert.Equal(B + 1, info.Version);
        Assert.True(info.FromUpdate);
        Assert.NotNull(info.StoredAtUtc);
        Assert.Equal(RuleVerdict.Protected, rules.Classify(new ItemFacts("Foo Remote", null, null, null)));
        Assert.Equal(RuleVerdict.Protected, rules.Classify(new ItemFacts("Dell Update", null, null, null)));
        Assert.StartsWith($"Regeln: Version {B + 1} vom ", info.DisplayText);

        // a cached copy of the bundled version (or older) is not used
        Directory.Delete(StoreDir, true);
        var same = Versioned(B, protectedJson: "[{\"name\":\"Foo Remote\"}]");
        Assert.Equal(RulesAcceptance.Accepted, Store().TryAccept(same, Sign(same), 0, V030));
        (rules, info) = RulesLoader.Load(RuleSet.Default, Store(), V030);
        Assert.Same(RuleSet.Default, rules);
        Assert.False(info.FromUpdate);
        Assert.Equal($"Regeln: Version {B} (mitgeliefert)", info.DisplayText);
    }

    [Fact]
    public void Load_ignores_tampered_oversized_or_untrusted_cache()
    {
        var jsonPath = Path.Combine(StoreDir, RulesStore.JsonName);
        var sigPath = Path.Combine(StoreDir, RulesStore.SigName);
        var v2 = Versioned(B + 1, protectedJson: "[{\"name\":\"Foo Remote\"}]");

        void AssertBundled(RulesStore s)
        {
            var (rules, info) = RulesLoader.Load(RuleSet.Default, s, V030);
            Assert.Same(RuleSet.Default, rules);
            Assert.False(info.FromUpdate);
            Assert.Equal(B, info.Version);
        }

        // edited after accept
        Assert.Equal(RulesAcceptance.Accepted, Store().TryAccept(v2, Sign(v2), B, V030));
        var text = File.ReadAllText(jsonPath).Replace("Foo Remote", "Foo Remotf");
        File.WriteAllText(jsonPath, text);
        AssertBundled(Store());
        Assert.Null(Store().LoadVerified());

        // validly signed but larger than MaxBytes
        var padded = new byte[RulesStore.MaxBytes + 1];
        Array.Fill(padded, (byte)' ');
        v2.CopyTo(padded, 0);
        WriteCache(padded, Sign(padded));
        AssertBundled(Store());

        // owner not trusted
        WriteCache(v2, Sign(v2));
        Assert.NotNull(Store().LoadVerified());
        AssertBundled(Store(_ => false));

        // signature missing
        File.Delete(sigPath);
        AssertBundled(Store());

        // nothing cached at all
        Directory.Delete(StoreDir, true);
        AssertBundled(Store());
    }

    [Fact]
    public void Load_rejects_cache_needing_newer_app()
    {
        var v2 = Versioned(B + 1, "9.0.0");
        WriteCache(v2, Sign(v2));
        var (rules, info) = RulesLoader.Load(RuleSet.Default, Store(), V030);
        Assert.Same(RuleSet.Default, rules);
        Assert.False(info.FromUpdate);
    }

    // ---- Fetcher / Updater ----

    sealed class FakeHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public List<Uri> Requests { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(r.RequestUri!);
            return Task.FromResult(_f(r));
        }
    }

    static HttpResponseMessage Bytes(byte[] b, bool announce = true)
    {
        var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        if (!announce) m.Content.Headers.ContentLength = null;
        return m;
    }

    static HttpResponseMessage Redirect(string loc)
    {
        var m = new HttpResponseMessage(HttpStatusCode.Found);
        m.Headers.Location = new Uri(loc, UriKind.RelativeOrAbsolute);
        return m;
    }

    [Fact]
    public async Task Fetcher_reads_both_files_with_limit_and_host_policy()
    {
        var json = Versioned(2);
        const string sig = "c2lnbmF0dXJl";
        var h = new FakeHandler(r => r.RequestUri == RulesFetcher.JsonUrl ? Bytes(json)
            : r.RequestUri == RulesFetcher.SigUrl ? Bytes(Encoding.ASCII.GetBytes(sig))
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var (j, s) = await new RulesFetcher(h).FetchAsync(default);
        Assert.Equal(json, j);
        Assert.Equal(sig, s);
        Assert.All(h.Requests, u => Assert.True(HostPolicy.Rules.IsAllowed(u)));

        h = new FakeHandler(r => r.RequestUri == RulesFetcher.JsonUrl ? Redirect("https://objects.githubusercontent.com/r") : Bytes(json));
        (j, _) = await new RulesFetcher(h).FetchAsync(default);
        Assert.Equal(json, j);
        Assert.Contains(h.Requests, u => u.Host == "objects.githubusercontent.com");

        h = new FakeHandler(r => r.RequestUri == RulesFetcher.JsonUrl ? Redirect("https://evil.com/r") : Bytes(json));
        await Assert.ThrowsAsync<UpdateDownloadException>(() => new RulesFetcher(h).FetchAsync(default));
        Assert.DoesNotContain(h.Requests, u => u.Host == "evil.com");

        h = new FakeHandler(_ => Bytes(new byte[RulesStore.MaxBytes + 1], announce: false));
        await Assert.ThrowsAsync<UpdateDownloadException>(() => new RulesFetcher(h).FetchAsync(default));
    }

    [Fact]
    public async Task Updater_reports_messages()
    {
        var json = Versioned(2);
        var sig = Sign(json);
        var h = new FakeHandler(r => r.RequestUri == RulesFetcher.JsonUrl ? Bytes(json) : Bytes(Encoding.ASCII.GetBytes(sig)));
        var updater = new RulesUpdater(new RulesFetcher(h), Store(), 1, V030);
        var r = await updater.RunAsync(default);
        Assert.Equal(RulesAcceptance.Accepted, r.Acceptance);
        Assert.Equal("Neue Regeln (Version 2) geladen. Sie gelten ab dem nächsten Start.", r.Message);

        // the updater now knows version 2
        r = await updater.RunAsync(default);
        Assert.Equal(RulesAcceptance.NotNewer, r.Acceptance);
        Assert.Equal("Die Regeln sind aktuell.", r.Message);

        var down = new FakeHandler(_ => throw new HttpRequestException("weg"));
        r = await new RulesUpdater(new RulesFetcher(down), Store(), 1, V030).RunAsync(default);
        Assert.Null(r.Acceptance);
        Assert.StartsWith("Regeln konnten nicht geladen werden", r.Message);
    }

    // ---- ReleaseTool ----

    [Fact]
    public void ReleaseCli_sign_rules_and_verify_rules()
    {
        var keys = Path.Combine(_root, "keys");
        var (priv, pub) = ReleaseSigner.GenerateKeyPair(keys);
        var rules = Path.Combine(_root, "rules.json");
        File.WriteAllBytes(rules, Versioned(2));

        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, ReleaseCli.Run(["sign-rules", priv, rules], o, e));
        Assert.True(File.Exists(rules + ".sig"));
        Assert.Equal(0, ReleaseCli.Run(["verify-rules", pub, rules, rules + ".sig"], o, e));

        var b = File.ReadAllBytes(rules);
        b[3] ^= 1;
        File.WriteAllBytes(rules, b);
        Assert.Equal(1, ReleaseCli.Run(["verify-rules", pub, rules, rules + ".sig"], o, e));
    }
}
