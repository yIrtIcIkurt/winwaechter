using WinWaechter.Core.Changes;

namespace WinWaechter.Core.Model;

public enum FindingArea { Autostart, Dienst, Aufgabe, Speicher, Prozess, Verbindung, Defender, Einstellung }

public enum Rating { Info, Auffaellig, Verdaechtig, Entbehrlich, Unerwuenscht }

public sealed record Finding(string Id, FindingArea Area, string Title, string Location,
    IReadOnlyList<string> Reasons, Rating Rating, bool Preselected, IChange? Change,
    long? SizeBytes = null, string? Publisher = null);
