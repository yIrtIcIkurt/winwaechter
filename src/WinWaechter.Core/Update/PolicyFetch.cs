using System.Net;

namespace WinWaechter.Core.Update;

// Bounded GET with manual redirects: every hop is checked against the policy, at most 5 redirects,
// Content-Length and the stream are both capped at max. Errors become UpdateDownloadException (German).
// The client must not follow redirects itself (see UpdateHttp.CreateClient).
internal static class PolicyFetch
{
    const int MaxRedirects = 5;

    public static async Task FetchAsync(HttpClient http, Uri url, HostPolicy policy, Stream dst, long max,
        IProgress<double>? p, CancellationToken ct)
    {
        try
        {
            var current = url;
            for (var hop = 0; ; hop++)
            {
                if (!policy.IsAllowed(current))
                    throw new UpdateDownloadException("Ungültige Download-Adresse im Release.");

                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.UserAgent.ParseAdd(UpdateChecker.UserAgent);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

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
