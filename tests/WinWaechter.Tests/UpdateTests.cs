using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinWaechter.Core.Update;

namespace WinWaechter.Tests;

public sealed class UpdateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ww-upd-" + Guid.NewGuid().ToString("N"));
    readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly SemVer V011 = new(0, 1, 1);
    static readonly SemVer V010 = new(0, 1, 0);

    public UpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    string PubPem => _key.ExportSubjectPublicKeyInfoPem();

    // Builds setup + signed sums/sig for the given version.
    (string sums, string sig, string setup, UpdateInfo info) MakeRelease(SemVer v, byte[]? content = null)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, $"WinWaechter-Setup-{v}.exe");
        File.WriteAllBytes(setup, content ?? Encoding.UTF8.GetBytes("setup-bytes-" + v));
        var (sums, sig) = ReleaseSigner.Sign(_key, v, setup, dir);
        return (sums, sig, setup, Info(v, Path.GetFileName(setup)));
    }

    static UpdateInfo Info(SemVer v, string name) => new(v, "notes",
        new Uri("https://github.com/x/" + name), new Uri("https://github.com/x/SHA256SUMS"),
        new Uri("https://github.com/x/SHA256SUMS.sig"), name);

    // Signs arbitrary sums text (for malformed-format tests).
    (string sums, string sig) SignRaw(string text)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var sums = Path.Combine(dir, "SHA256SUMS");
        var sig = Path.Combine(dir, "SHA256SUMS.sig");
        File.WriteAllBytes(sums, bytes);
        File.WriteAllText(sig, Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        return (sums, sig);
    }

    // ---- SemVer / HostPolicy ----

    [Fact]
    public void SemVer_compare_and_parse()
    {
        Assert.True(SemVer.TryParse("v0.1.10", out var a));
        Assert.True(SemVer.TryParse("0.1.9", out var b));
        Assert.True(a > b);
        Assert.Equal("0.1.10", a.ToString());
        foreach (var bad in new[] { "", "1.2", "1.2.3.4", "a.b.c", "1.2.-3", "1.2.3-beta", "vv1.2.3", "1. 2.3" })
            Assert.False(SemVer.TryParse(bad, out _), bad);
    }

    [Fact]
    public void HostPolicy_rejects_http_and_foreign_hosts()
    {
        Assert.False(HostPolicy.IsAllowed(new Uri("http://github.com/x")));
        Assert.False(HostPolicy.IsAllowed(new Uri("https://evil.com")));
        Assert.True(HostPolicy.IsAllowed(new Uri("https://release-assets.githubusercontent.com/x")));
        Assert.True(HostPolicy.IsAllowed(new Uri("https://GitHub.com/x")));
        Assert.False(HostPolicy.IsAllowed(new Uri("https://github.com.evil.com/x")));
        Assert.False(HostPolicy.IsAllowed(new Uri("https://evil.com/github.com")));
        Assert.False(HostPolicy.IsAllowed(new Uri("https://user:pw@github.com/x")));
        Assert.False(HostPolicy.IsAllowed(new Uri("https://github.com:8443/x")));
        Assert.False(HostPolicy.IsAllowed(new Uri("ftp://github.com/x")));
        Assert.False(HostPolicy.IsAllowed(null));
    }

    // ---- Verifier ----

    [Fact]
    public void Valid_release_verifies()
    {
        var r = MakeRelease(V011);
        var v = new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, r.info);
        Assert.Equal(V011, v.Version);
        Assert.Equal(Path.GetFullPath(r.setup), v.SetupPath);
    }

    [Fact]
    public void Valid_release_with_crlf_sums_verifies()
    {
        var r = MakeRelease(V011);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r.setup))).ToLowerInvariant();
        var (sums, sig) = SignRaw($"version: 0.1.1\r\n{hash}  {r.info.SetupName}\r\n");
        Assert.Equal(V011, new ReleaseVerifier(PubPem, V010).Verify(sums, sig, r.setup, r.info).Version);
    }

    [Fact]
    public void Tampered_setup_rejected()
    {
        var r = MakeRelease(V011);
        var bytes = File.ReadAllBytes(r.setup);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(r.setup, bytes);
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, r.info));
        Assert.Contains("Prüfsumme", ex.Message);
    }

    [Fact]
    public void Tampered_sums_rejected_by_signature()
    {
        var r = MakeRelease(V011);
        var other = MakeRelease(V011, Encoding.UTF8.GetBytes("evil"));
        // evil sums with the genuine signature
        File.Copy(other.sums, r.sums, true);
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, r.info));
        Assert.Contains("Signatur", ex.Message);
    }

    [Fact]
    public void Wrong_key_signature_rejected()
    {
        var r = MakeRelease(V011);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier(other.ExportSubjectPublicKeyInfoPem(), V010).Verify(r.sums, r.sig, r.setup, r.info));
        Assert.Contains("Signatur", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!!")]
    [InlineData("AAAA")]
    public void Garbage_signature_rejected(string sigText)
    {
        var r = MakeRelease(V011);
        File.WriteAllText(r.sig, sigText);
        Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, r.info));
    }

    [Fact]
    public void Garbage_public_key_rejected()
    {
        var r = MakeRelease(V011);
        Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier("PLACEHOLDER", V010).Verify(r.sums, r.sig, r.setup, r.info));
    }

    [Fact]
    public void Older_or_equal_version_rejected()
    {
        var r = MakeRelease(V011);
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, V011).Verify(r.sums, r.sig, r.setup, r.info));
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, new SemVer(0, 2, 0)).Verify(r.sums, r.sig, r.setup, r.info));
    }

    [Fact]
    public void Version_line_mismatch_rejected()
    {
        var r = MakeRelease(V011);
        var info = r.info with { Version = new SemVer(0, 1, 2) };
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, info));
    }

    [Fact]
    public void Setup_name_not_listed_rejected()
    {
        var r = MakeRelease(V011);
        var info = r.info with { SetupName = "WinWaechter-Setup-0.1.2.exe" };
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, info));
    }

    [Theory]
    [InlineData("version: 0.1.1\n")]                                                    // no hash line
    [InlineData("0.1.1\nHASH  WinWaechter-Setup-0.1.1.exe\n")]                          // no version prefix
    [InlineData("version: v0.1.1\nHASH  WinWaechter-Setup-0.1.1.exe\n")]                // v in signed version
    [InlineData("version: 0.1.1\nHASH  WinWaechter-Setup-0.1.1.exe\nextra\n")]          // extra content
    [InlineData("version: 0.1.1\n\nHASH  WinWaechter-Setup-0.1.1.exe\n")]               // blank line
    [InlineData("version: 0.1.1\nHASH WinWaechter-Setup-0.1.1.exe\n")]                  // single space
    [InlineData("version: 0.1.1\nHASH  ..\\WinWaechter-Setup-0.1.1.exe\n")]             // path separator
    [InlineData("version: 0.1.1\nHASH  WinWaechter-Setup-0.1.1.exe\nHASH  WinWaechter-Setup-0.1.1.exe\n")] // duplicate
    [InlineData("version: 0.1.1\nHASH  WinWaechter-Setup-0.1.1.exe\n\n")]               // two trailing newlines
    public void Malformed_sums_rejected_even_when_signed(string template)
    {
        var r = MakeRelease(V011);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r.setup))).ToLowerInvariant();
        var (sums, sig) = SignRaw(template.Replace("HASH", hash));
        var ex = Assert.Throws<UpdateVerificationException>(() =>
            new ReleaseVerifier(PubPem, V010).Verify(sums, sig, r.setup, r.info));
        Assert.Contains("Format", ex.Message);
    }

    [Fact]
    public void Uppercase_hex_rejected()
    {
        var r = MakeRelease(V011);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(r.setup))).ToUpperInvariant();
        var (sums, sig) = SignRaw($"version: 0.1.1\n{hash}  {r.info.SetupName}\n");
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, V010).Verify(sums, sig, r.setup, r.info));
    }

    [Fact]
    public void Oversized_sums_rejected()
    {
        var r = MakeRelease(V011);
        File.WriteAllBytes(r.sums, new byte[ReleaseVerifier.MaxSmallFileBytes + 1]);
        Assert.Throws<UpdateVerificationException>(() => new ReleaseVerifier(PubPem, V010).Verify(r.sums, r.sig, r.setup, r.info));
    }

    // ---- Checker ----

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

    static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> f, SemVer? installed = null)
        => new(new HttpClient(new FakeHandler(f)), installed ?? V010);

    static string ReleaseJson(string tag = "v0.1.1", string extra = "", string? setupUrl = null, bool setup = true, bool sums = true, bool sig = true)
    {
        var v = tag.TrimStart('v');
        var assets = new List<string>();
        if (setup) assets.Add($"{{\"name\":\"WinWaechter-Setup-{v}.exe\",\"browser_download_url\":\"{setupUrl ?? $"https://github.com/o/r/releases/download/{tag}/WinWaechter-Setup-{v}.exe"}\"}}");
        assets.Add("{\"name\":\"other.zip\",\"browser_download_url\":\"https://github.com/o/r/other.zip\"}");
        if (sums) assets.Add($"{{\"name\":\"SHA256SUMS\",\"browser_download_url\":\"https://github.com/o/r/releases/download/{tag}/SHA256SUMS\"}}");
        if (sig) assets.Add($"{{\"name\":\"SHA256SUMS.sig\",\"browser_download_url\":\"https://github.com/o/r/releases/download/{tag}/SHA256SUMS.sig\"}}");
        return $"{{\"tag_name\":\"{tag}\",\"body\":\"Neu: Dinge\"{extra},\"assets\":[{string.Join(",", assets)}]}}";
    }

    [Fact]
    public async Task Checker_maps_403_to_rate_limit_message()
    {
        var r = await Checker(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("GitHub-Abfragelimit erreicht, später erneut versuchen.", r.Message);
        r = await Checker(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)).CheckAsync(default);
        Assert.Equal("GitHub-Abfragelimit erreicht, später erneut versuchen.", r.Message);
    }

    [Fact]
    public async Task Checker_maps_404()
    {
        var r = await Checker(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("Noch kein Release veröffentlicht.", r.Message);
    }

    [Fact]
    public async Task Checker_offline()
    {
        var r = await Checker(_ => throw new HttpRequestException("down")).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("Keine Verbindung zu GitHub.", r.Message);
    }

    [Fact]
    public async Task Checker_rethrows_real_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Checker(_ => throw new TaskCanceledException()).CheckAsync(cts.Token));
    }

    [Fact]
    public async Task Checker_picks_assets_by_name()
    {
        var h = new FakeHandler(_ => Json(ReleaseJson()));
        var r = await new UpdateChecker(new HttpClient(h), V010).CheckAsync(default);
        Assert.NotNull(r.Available);
        var i = r.Available!;
        Assert.Equal(V011, i.Version);
        Assert.Equal("Neu: Dinge", i.Notes);
        Assert.Equal("WinWaechter-Setup-0.1.1.exe", i.SetupName);
        Assert.EndsWith("/WinWaechter-Setup-0.1.1.exe", i.SetupUrl.AbsoluteUri);
        Assert.EndsWith("/SHA256SUMS", i.SumsUrl.AbsoluteUri);
        Assert.EndsWith("/SHA256SUMS.sig", i.SigUrl.AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/yIrtIcIkurt/winwaechter/releases/latest", h.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Checker_same_or_older_is_up_to_date()
    {
        var r = await Checker(_ => Json(ReleaseJson()), V011).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("WinWächter ist aktuell.", r.Message);
    }

    [Theory]
    [InlineData(",\"draft\":true")]
    [InlineData(",\"prerelease\":true")]
    public async Task Checker_ignores_draft_and_prerelease(string extra)
    {
        var r = await Checker(_ => Json(ReleaseJson(extra: extra))).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("Noch kein Release veröffentlicht.", r.Message);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Checker_missing_asset_is_incomplete(bool setup, bool sums, bool sig)
    {
        var r = await Checker(_ => Json(ReleaseJson(setup: setup, sums: sums, sig: sig))).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("Release unvollständig.", r.Message);
    }

    [Theory]
    [InlineData("https://evil.com/WinWaechter-Setup-0.1.1.exe")]
    [InlineData("http://github.com/o/r/WinWaechter-Setup-0.1.1.exe")]
    [InlineData("https://github.com.evil.com/x.exe")]
    [InlineData("not a url")]
    public async Task Checker_rejects_foreign_asset_url(string url)
    {
        var r = await Checker(_ => Json(ReleaseJson(setupUrl: url))).CheckAsync(default);
        Assert.Null(r.Available);
        Assert.Equal("Ungültige Download-Adresse im Release.", r.Message);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"tag_name\":\"banana\"}")]
    public async Task Checker_handles_garbage(string json)
    {
        var r = await Checker(_ => Json(json)).CheckAsync(default);
        Assert.Null(r.Available);
    }

    [Fact]
    public void Checker_rejects_bad_repo_name()
        => Assert.Throws<ArgumentException>(() => new UpdateChecker(new HttpClient(), V010, "a/b/../../c"));

    // ---- Downloader ----

    static HttpResponseMessage Bytes(byte[] b, bool announce = true)
    {
        var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        if (!announce) m.Content.Headers.ContentLength = null;
        return m;
    }

    static HttpResponseMessage Redirect(string loc, HttpStatusCode c = HttpStatusCode.Found)
    {
        var m = new HttpResponseMessage(c);
        m.Headers.Location = new Uri(loc, UriKind.RelativeOrAbsolute);
        return m;
    }

    string TargetDir() => Path.Combine(_root, "Updates");

    [Fact]
    public async Task Downloader_follows_allowed_redirects_and_writes_files()
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/x/WinWaechter-Setup-0.1.1.exe" => Redirect("https://objects.githubusercontent.com/a"),
            "/a" => Redirect("/b"), // relative
            "/b" => Bytes(new byte[] { 1, 2, 3 }),
            _ => Bytes(Encoding.ASCII.GetBytes("small")),
        });
        var prog = new List<double>();
        var (sums, sig, setup) = await new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), new Progress<double>(prog.Add), default);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(setup));
        Assert.Equal("small", File.ReadAllText(sums));
        Assert.Equal("small", File.ReadAllText(sig));
        Assert.StartsWith(TargetDir() + Path.DirectorySeparatorChar + "0.1.1-", Path.GetDirectoryName(setup)! + Path.DirectorySeparatorChar);
        Assert.All(h.Requests, u => Assert.True(HostPolicy.IsAllowed(u)));
    }

    [Theory]
    [InlineData("https://evil.com/x")]
    [InlineData("http://github.com/x")]
    [InlineData("https://github.com:444/x")]
    public async Task Downloader_rejects_redirect_to_foreign_host_and_cleans_up(string loc)
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/x/SHA256SUMS" ? Bytes(new byte[] { 1 }) : Redirect(loc));
        await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), null, default));
        Assert.DoesNotContain(h.Requests, u => u.Host == "evil.com");
        Assert.Empty(Directory.GetFileSystemEntries(TargetDir()));
    }

    [Fact]
    public async Task Downloader_limits_redirects_to_five()
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        var n = 0;
        var h = new FakeHandler(_ => Redirect($"https://github.com/r{++n}"));
        await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), null, default));
        Assert.Equal(6, h.Requests.Count); // first request + 5 redirects
        Assert.Empty(Directory.GetFileSystemEntries(TargetDir()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Downloader_rejects_oversized_small_file(bool announce)
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        var h = new FakeHandler(_ => Bytes(new byte[64 * 1024 + 1], announce));
        await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), null, default));
        Assert.Empty(Directory.GetFileSystemEntries(TargetDir()));
    }

    [Fact]
    public async Task Downloader_rejects_http_error_and_cleans_up()
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<UpdateDownloadException>(() =>
            new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), null, default));
        Assert.Empty(Directory.GetFileSystemEntries(TargetDir()));
    }

    [Fact]
    public async Task Downloader_cancel_cleans_up()
    {
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler(_ => { cts.Cancel(); return Bytes(new byte[] { 1 }); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(new HttpClient(h)).DownloadAsync(info, TargetDir(), null, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(TargetDir()));
    }

    [Fact]
    public async Task Downloader_refuses_foreign_url_in_info_and_bad_name()
    {
        var bad = Info(V011, "WinWaechter-Setup-0.1.1.exe") with { SetupUrl = new Uri("https://evil.com/a.exe") };
        var d = new UpdateDownloader(new HttpClient(new FakeHandler(_ => Bytes(new byte[1]))));
        await Assert.ThrowsAsync<UpdateDownloadException>(() => d.DownloadAsync(bad, TargetDir(), null, default));
        var badName = Info(V011, "..\\evil.exe");
        await Assert.ThrowsAsync<UpdateDownloadException>(() => d.DownloadAsync(badName, TargetDir(), null, default));
    }

    // ---- Installer ----

    [Fact]
    public void Installer_builds_start_info_and_requires_absolute_path()
    {
        var path = Path.Combine(_root, "WinWaechter-Setup-0.1.1.exe");
        var psi = UpdateInstaller.BuildStartInfo(new VerifiedUpdate(path, V011));
        Assert.Equal(path, psi.FileName);
        Assert.Equal("/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.Throws<ArgumentException>(() => UpdateInstaller.BuildStartInfo(new VerifiedUpdate("setup.exe", V011)));
        Assert.Throws<ArgumentException>(() => UpdateInstaller.BuildStartInfo(new VerifiedUpdate(@"\setup.exe", V011)));
    }

    // ---- ReleaseSigner ----

    [Fact]
    public void Keygen_writes_pair_and_refuses_overwrite_then_sign_roundtrips()
    {
        var keys = Path.Combine(_root, "keys");
        var (priv, pub) = ReleaseSigner.GenerateKeyPair(keys);
        Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(priv));
        Assert.Contains("BEGIN PUBLIC KEY", File.ReadAllText(pub));
        Assert.Throws<IOException>(() => ReleaseSigner.GenerateKeyPair(keys));

        var dir = Path.Combine(_root, "rel");
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, "WinWaechter-Setup-0.1.1.exe");
        File.WriteAllText(setup, "payload");
        var (sums, sig) = ReleaseSigner.Sign(priv, V011, setup, Path.Combine(_root, "out"));
        var info = Info(V011, "WinWaechter-Setup-0.1.1.exe");
        Assert.Equal(V011, new ReleaseVerifier(File.ReadAllText(pub), V010).Verify(sums, sig, setup, info).Version);
        Assert.StartsWith("version: 0.1.1\n", File.ReadAllText(sums));
    }

    [Fact]
    public void Sign_rejects_wrong_setup_file_name()
    {
        var f = Path.Combine(_root, "setup.exe");
        File.WriteAllText(f, "x");
        Assert.Throws<ArgumentException>(() => ReleaseSigner.Sign(_key, V011, f, Path.Combine(_root, "o")));
    }

    [Fact]
    public void Embedded_public_key_is_valid_p256()
    {
        using var k = ECDsa.Create();
        k.ImportFromPem(UpdateKeys.PublicKeyPem);
        Assert.Equal(256, k.KeySize);
    }
}
