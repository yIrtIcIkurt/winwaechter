using System.Security.Cryptography;

namespace WinWaechter.Core.Update;

// Downloads setup + SHA256SUMS + signature into a fresh subfolder of targetDir.
// The APP must create targetDir with SecureDataDir.Ensure(%ProgramData%\WinWaechter\Updates) first;
// this class does not, so tests can use temp folders. Pass UpdateHttp.CreateHandler() (no auto-redirect, enforced):
// redirects are followed by PolicyFetch, and every hop is checked against HostPolicy.Updates.
public sealed class UpdateDownloader
{
    public const long MaxSetupBytes = 300L * 1024 * 1024;
    public const long MaxSmallBytes = 64 * 1024;

    readonly HttpClient _http;

    public UpdateDownloader(HttpMessageHandler handler) { _http = UpdateHttp.CreateClient(handler, TimeSpan.FromMinutes(30)); }

    public async Task<(string sums, string sig, string setup)> DownloadAsync(
        UpdateInfo i, string targetDir, IProgress<double>? p, CancellationToken ct)
    {
        var policy = HostPolicy.Updates;
        if (!policy.IsAllowed(i.SetupUrl) || !policy.IsAllowed(i.SumsUrl) || !policy.IsAllowed(i.SigUrl))
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
        await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await PolicyFetch.FetchAsync(_http, url, HostPolicy.Updates, dst, max, p, ct).ConfigureAwait(false);
    }
}
