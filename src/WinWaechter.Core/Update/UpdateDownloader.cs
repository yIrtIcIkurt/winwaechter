using System.Net;
using System.Security.Cryptography;

namespace WinWaechter.Core.Update;

// Downloads setup + SHA256SUMS + signature into a fresh subfolder of targetDir.
// The APP must create targetDir with SecureDataDir.Ensure(%ProgramData%\WinWaechter\Updates) first;
// this class does not, so tests can use temp folders. Pass UpdateHttp.CreateHandler() (no auto-redirect, enforced):
// redirects are followed here by hand, and every hop is checked against HostPolicy.
public sealed class UpdateDownloader
{
    public const long MaxSetupBytes = 300L * 1024 * 1024;
    public const long MaxSmallBytes = 64 * 1024;
    const int MaxRedirects = 5;

    readonly HttpClient _http;

    public UpdateDownloader(HttpMessageHandler handler) { _http = UpdateHttp.CreateClient(handler, TimeSpan.FromMinutes(30)); }

    public async Task<(string sums, string sig, string setup)> DownloadAsync(
        UpdateInfo i, string targetDir, IProgress<double>? p, CancellationToken ct)
    {
        if (!HostPolicy.IsAllowed(i.SetupUrl) || !HostPolicy.IsAllowed(i.SumsUrl) || !HostPolicy.IsAllowed(i.SigUrl))
            throw new UpdateDownloadException("Ungültige Download-Adresse im Release.");
        if (!SumsFile.IsPlainFileName(i.SetupName))
            throw new UpdateDownloadException("Ungültiger Dateiname im Release.");

        var dir = Path.Combine(targetDir, $"{i.Version}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}");
        Directory.CreateDirectory(dir);
        var sums = Path.Combine(dir, "SHA256SUMS");
        var sig = Path.Combine(dir, "SHA256SUMS.sig");
        var setup = Path.Combine(dir, i.SetupName);
        try
        {
            await FetchAsync(i.SumsUrl, sums, MaxSmallBytes, null, ct).ConfigureAwait(false);
            await FetchAsync(i.SigUrl, sig, MaxSmallBytes, null, ct).ConfigureAwait(false);
            await FetchAsync(i.SetupUrl, setup, MaxSetupBytes, p, ct).ConfigureAwait(false);
            return (sums, sig, setup);
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
            throw;
        }
    }

    async Task FetchAsync(Uri url, string path, long max, IProgress<double>? p, CancellationToken ct)
    {
        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                if (!HostPolicy.IsAllowed(current))
                    throw new UpdateDownloadException("Ungültige Download-Adresse im Release.");

                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop >= MaxRedirects) throw new UpdateDownloadException("Zu viele Weiterleitungen beim Download.");
                    var loc = resp.Headers.Location ?? throw new UpdateDownloadException("Ungültige Weiterleitung beim Download.");
                    current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new UpdateDownloadException($"Download fehlgeschlagen (HTTP {(int)resp.StatusCode}).");

                var len = resp.Content.Headers.ContentLength;
                if (len > max) throw new UpdateDownloadException("Die Datei ist größer als erlaubt.");

                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await BoundedRead.CopyAsync(src, dst, max, len, p, ct).ConfigureAwait(false);
                p?.Report(1.0);
                return;
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) // HTTP timeout
        {
            throw new UpdateDownloadException("Keine Verbindung zu GitHub.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateDownloadException("Keine Verbindung zu GitHub.", e);
        }
    }
}
