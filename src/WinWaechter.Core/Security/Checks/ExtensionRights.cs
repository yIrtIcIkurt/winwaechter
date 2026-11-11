namespace WinWaechter.Core.Security.Checks;

/// <summary>
/// Plain German summary of what an extension may do, from its API permissions and host patterns.
/// Raw permission strings come from files anyone can write; they are only matched, never shown.
/// </summary>
public static class ExtensionRights
{
    public const string AllSites = "Darf alle Webseiten lesen und ändern", None = "Keine besonderen Rechte";

    static readonly (string[] Names, string Text)[] Apis =
    [
        (["nativeMessaging"], "Darf mit Programmen auf dem PC sprechen"),
        (["proxy"], "Darf Ihren Internetverkehr umleiten"),
        (["webRequest", "webRequestBlocking", "declarativeNetRequest"], "Darf Webanfragen mitlesen oder umleiten"),
        (["cookies"], "Darf Cookies lesen"),
        (["history"], "Darf den Verlauf lesen"),
        (["tabs"], "Sieht Ihre geöffneten Tabs"),
        (["downloads"], "Darf Downloads verwalten"),
        (["management"], "Darf andere Erweiterungen verwalten"),
        (["clipboardRead"], "Darf die Zwischenablage lesen"),
    ];

    /// <summary>True for a pattern that covers every site: &lt;all_urls&gt; or a host of "*" (e.g. *://*/*, https://*/*).</summary>
    public static bool IsAllSites(string pattern)
    {
        if (pattern == "<all_urls>") return true;
        var sep = pattern.IndexOf("://", StringComparison.Ordinal);
        if (sep < 0) return false;
        var rest = pattern[(sep + 3)..];
        var slash = rest.IndexOf('/');
        return (slash < 0 ? rest : rest[..slash]) == "*";
    }

    /// <summary>A host pattern (as found mixed into MV2 "permissions"): &lt;all_urls&gt; or anything with "://".</summary>
    public static bool IsHostPattern(string p) => p == "<all_urls>" || p.Contains("://", StringComparison.Ordinal);

    public static IReadOnlyList<string> Describe(IEnumerable<string> permissions, IEnumerable<string> hosts)
    {
        var perms = new HashSet<string>(permissions, StringComparer.Ordinal);
        var hostList = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lines = new List<string>();
        if (hostList.Any(IsAllSites)) lines.Add(AllSites);
        else if (hostList.Count == 1) lines.Add("Darf auf 1 Webseite zugreifen");
        else if (hostList.Count > 1) lines.Add($"Darf auf {hostList.Count} Webseiten zugreifen");
        foreach (var (names, text) in Apis)
            if (names.Any(perms.Contains)) lines.Add(text);
        return lines.Count == 0 ? [None] : lines;
    }
}
