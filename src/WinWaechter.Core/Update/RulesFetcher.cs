using System.Text;
using WinWaechter.Core.Rules;

namespace WinWaechter.Core.Update;

// Downloads rules/rules.json and its signature from the repo. Pass UpdateHttp.CreateHandler() (no auto-redirect).
// Only HostPolicy.Rules hosts, at most RulesStore.MaxBytes each. Nothing is verified here (see RulesStore).
public sealed class RulesFetcher
{
    public static readonly Uri JsonUrl = new("https://raw.githubusercontent.com/yIrtIcIkurt/winwaechter/main/rules/rules.json");
    public static readonly Uri SigUrl = new("https://raw.githubusercontent.com/yIrtIcIkurt/winwaechter/main/rules/rules.json.sig");

    readonly HttpClient _http;

    public RulesFetcher(HttpMessageHandler handler) { _http = UpdateHttp.CreateClient(handler, TimeSpan.FromSeconds(30)); }

    public async Task<(byte[] Json, string Sig)> FetchAsync(CancellationToken ct)
    {
        var json = await GetAsync(JsonUrl, ct).ConfigureAwait(false);
        var sig = await GetAsync(SigUrl, ct).ConfigureAwait(false);
        return (json, Encoding.ASCII.GetString(sig));
    }

    async Task<byte[]> GetAsync(Uri url, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await PolicyFetch.FetchAsync(_http, url, HostPolicy.Rules, ms, RulesStore.MaxBytes, null, ct).ConfigureAwait(false);
        return ms.ToArray();
    }
}
