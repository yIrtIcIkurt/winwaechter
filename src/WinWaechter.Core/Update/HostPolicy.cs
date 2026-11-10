namespace WinWaechter.Core.Update;

// Every URL the updater (or the rules fetch) touches must pass this:
// https, exact host from the list, no userinfo, default port.
public sealed class HostPolicy
{
    static readonly string[] UpdateHosts =
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    readonly string[] _hosts;

    public HostPolicy(IEnumerable<string> hosts) => _hosts = hosts.ToArray();

    // App updates (releases API and release assets).
    public static HostPolicy Updates { get; } = new(UpdateHosts);

    // Rule updates: the update hosts plus raw.githubusercontent.com (rules/rules.json in the repo).
    public static HostPolicy Rules { get; } = new(UpdateHosts.Append("raw.githubusercontent.com"));

    public bool IsAllowed(Uri? u)
    {
        if (u is null || !u.IsAbsoluteUri) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        if (u.UserInfo.Length != 0) return false;
        if (!u.IsDefaultPort) return false;
        foreach (var h in _hosts)
            if (string.Equals(u.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
