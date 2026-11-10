using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinWaechter.Core.Update;

// Asks GitHub for the latest release. Pass UpdateHttp.CreateHandler() (no auto-redirect; enforced).
public sealed class UpdateChecker
{
    const long MaxJsonBytes = 1_000_000;
    internal const string UserAgent = "WinWaechter-Updater";

    readonly HttpClient _http;
    readonly SemVer _installed;
    readonly string _repo;

    public UpdateChecker(HttpMessageHandler handler, SemVer installed, string repo = "yIrtIcIkurt/winwaechter")
    {
        if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("Ungültiger Repository-Name.", nameof(repo));
        _http = UpdateHttp.CreateClient(handler, TimeSpan.FromSeconds(30));
        _installed = installed;
        _repo = repo;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        var url = new Uri($"https://api.github.com/repos/{_repo}/releases/latest");
        if (!HostPolicy.Updates.IsAllowed(url)) return Fail("Ungültige Download-Adresse im Release.");

        byte[] body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Fail("GitHub-Abfragelimit erreicht, später erneut versuchen.");
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Fail("Noch kein Release veröffentlicht.");
            if (!resp.IsSuccessStatusCode)
                return Fail($"GitHub antwortet mit einem Fehler (HTTP {code}).");

            if (resp.Content.Headers.ContentLength > MaxJsonBytes) return Fail("Ungültige Antwort von GitHub.");
            using var ms = new MemoryStream();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await BoundedRead.CopyAsync(s, ms, MaxJsonBytes, null, null, ct).ConfigureAwait(false);
            body = ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UpdateDownloadException) { return Fail("Ungültige Antwort von GitHub."); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return Fail("Keine Verbindung zu GitHub.");
        }

        try { return Evaluate(body); }
        catch (JsonException) { return Fail("Ungültige Antwort von GitHub."); }
    }

    UpdateCheckResult Evaluate(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Fail("Ungültige Antwort von GitHub.");

        if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            return Fail("Noch kein Release veröffentlicht.");

        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || !SemVer.TryParse(tag.GetString(), out var version))
            return Fail("Ungültige Versionsnummer im Release.");

        if (version <= _installed) return new UpdateCheckResult(null, "WinWächter ist aktuell.");

        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";

        var setupName = $"WinWaechter-Setup-{version}.exe";
        string? setup = null, sums = null, sig = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                if (!a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                switch (n.GetString())
                {
                    case var x when x == setupName: setup ??= u.GetString(); break;
                    case "SHA256SUMS": sums ??= u.GetString(); break;
                    case "SHA256SUMS.sig": sig ??= u.GetString(); break;
                }
            }
        }
        if (setup is null || sums is null || sig is null) return Fail("Release unvollständig.");

        if (!TryAllowed(setup, out var setupUri) || !TryAllowed(sums, out var sumsUri) || !TryAllowed(sig, out var sigUri))
            return Fail("Ungültige Download-Adresse im Release.");

        var info = new UpdateInfo(version, notes, setupUri!, sumsUri!, sigUri!, setupName);
        return new UpdateCheckResult(info, $"Version {version} ist verfügbar.");
    }

    static bool IsTrue(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static bool TryAllowed(string s, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || !HostPolicy.Updates.IsAllowed(u)) return false;
        uri = u;
        return true;
    }

    static UpdateCheckResult Fail(string message) => new(null, message);
}
