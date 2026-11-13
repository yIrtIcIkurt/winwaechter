using WinWaechter.Core.Changes;

namespace WinWaechter.Core.Model;

public enum FindingArea { Autostart, Dienst, Aufgabe, Speicher, Prozess, Verbindung, Defender, Einstellung }

public enum Rating { Info, Auffaellig, Verdaechtig, Entbehrlich, Unerwuenscht }

public sealed record Finding(string Id, FindingArea Area, string Title, string Location,
    IReadOnlyList<string> Reasons, Rating Rating, bool Preselected, IChange? Change,
    long? SizeBytes = null, string? Publisher = null, bool IsProtectedVendor = false, string? Group = null,
    string? ExePath = null, string? BlockingProcess = null);
// IsProtectedVendor: matched the protected list (name/path) but could not be confirmed by a valid
// signature. Such findings get no change and no "kill" action; only a Defender check is offered.
// Group: optional sub-heading on the Optimize page (one of FindingGroups); null = area heading only.
// ExePath: the normalized program path the scanner judged (null when there is none).
// BlockingProcess: a blocked browser cache names the browser process (msedge, chrome, opera, firefox; without ".exe").

public static class FindingGroups
{
    public const string BrowserCache = "Browser-Cache", WindowsRemnants = "Windows-Reste", OldInstallers = "Alte Installer",
        OrphanEntries = "Verwaiste Einträge", OrphanFolders = "Verwaiste Ordner", ContextMenu = "Rechtsklick-Menü";
}
