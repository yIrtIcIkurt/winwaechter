namespace WinWaechter.Core.Update;

// Every URL the updater touches must pass this: https, exact host, no userinfo, default port.
public static class HostPolicy
{
    static readonly string[] Hosts =
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    public static bool IsAllowed(Uri? u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        if (u.UserInfo.Length != 0) return false;
        if (!u.IsDefaultPort) return false;
        foreach (var h in Hosts)
            if (string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
