using System.Net;

namespace WinWaechter.Core.Update;

// The only handler the app should give to UpdateChecker/UpdateDownloader. Redirects are never followed
// automatically, so the downloader can check every hop against the HostPolicy.
public static class UpdateHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    internal static HttpClient CreateClient(HttpMessageHandler handler, TimeSpan timeout)
    {
        var follows = handler switch
        {
            SocketsHttpHandler s => s.AllowAutoRedirect,
            HttpClientHandler h => h.AllowAutoRedirect,
            _ => false, // custom/fake handlers do not redirect on their own
        };
        if (follows)
            throw new ArgumentException("Der HTTP-Handler darf Weiterleitungen nicht selbst folgen.", nameof(handler));
        var c = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateChecker.UserAgent);
        return c;
    }
}
