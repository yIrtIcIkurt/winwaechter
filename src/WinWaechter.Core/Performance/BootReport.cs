using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Performance;

/// <summary>One entry from the Diagnostics-Performance log. <see cref="Time"/> is UTC.</summary>
public sealed record BootEvent(int Id, DateTime Time, string? Name, long? BootTimeMs, long? DegradationMs,
    string? FileName = null);

/// <summary>FileName: the program's file name when the log gave both a friendly and a file name.</summary>
public sealed record BootDelay(string Name, TimeSpan Delay, string? FileName = null);

public interface IBootEventSource
{
    /// <summary>Events 100-103 created at or after <paramref name="since"/> (UTC), newest first.
    /// Throws <see cref="UnauthorizedAccessException"/> when reading is not allowed.</summary>
    IReadOnlyList<BootEvent> Read(DateTime since);
}

public sealed record BootReport(
    IReadOnlyList<TimeSpan> LastBoots,
    TimeSpan? Average,
    IReadOnlyList<BootDelay> Slowest,
    string? Message)
{
    public const string NoMeasurement = "Noch keine Messung. Windows misst die Startzeit nach einem Neustart.";
    public const string AccessDenied = "Nur mit Adminrechten lesbar.";
    public const string Unreadable = "Die Startzeit ließ sich nicht lesen.";
    const int Top = 5;
    const int Days = 30;

    static BootReport Empty(string message) => new([], null, [], message);

    /// <summary>All event times are UTC; the 30-day window is computed in UTC too.</summary>
    public static BootReport From(IReadOnlyList<BootEvent> events, DateTime now)
    {
        var cutoff = now.ToUniversalTime().AddDays(-Days);

        var boots = events
            .Where(e => e.Id == 100 && e.BootTimeMs is > 0)
            .OrderByDescending(e => e.Time)
            .Take(Top)
            .Select(e => TimeSpan.FromMilliseconds(e.BootTimeMs!.Value))
            .ToList();

        if (boots.Count == 0) return Empty(NoMeasurement);

        var average = TimeSpan.FromTicks((long)boots.Average(b => b.Ticks));

        var slowest = events
            .Where(e => e.Id is 101 or 102 or 103 && e.Time >= cutoff
                        && !string.IsNullOrWhiteSpace(e.Name) && e.DegradationMs is > 0)
            .GroupBy(e => e.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BootDelay(g.First().Name!, TimeSpan.FromMilliseconds(g.Max(e => e.DegradationMs!.Value)),
                g.Select(e => e.FileName).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))))
            .OrderByDescending(d => d.Delay)
            .Take(Top)
            .ToList();

        return new BootReport(boots, average, slowest, null);
    }

    public static BootReport Load(IBootEventSource src, DateTime now)
    {
        try
        {
            return From(src.Read(now.ToUniversalTime().AddDays(-Days)), now);
        }
        catch (UnauthorizedAccessException)
        {
            return Empty(AccessDenied);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Startzeit-Bericht konnte nicht gelesen werden.", ex);
            return Empty(Unreadable);
        }
    }
}

public sealed class WinBootEventSource : IBootEventSource
{
    const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    const int MaxEvents = 200;

    public IReadOnlyList<BootEvent> Read(DateTime since)
    {
        var xpath = "*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103) and TimeCreated[@SystemTime>='"
                    + since.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) + "']]]";
        var list = new List<BootEvent>();
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (EventRecord? rec = reader.ReadEvent(); rec is not null && list.Count < MaxEvents; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    var e = Parse(rec);
                    if (e is not null) list.Add(e);
                }
            }
        }
        catch (EventLogException ex) when (IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        return list;
    }

    static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex.InnerException is UnauthorizedAccessException
        || ex.HResult == unchecked((int)0x80070005)
        || (ex.InnerException?.HResult == unchecked((int)0x80070005))
        || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase);

    static BootEvent? Parse(EventRecord rec)
    {
        if (rec.TimeCreated is not { } time) return null;
        var doc = XDocument.Parse(rec.ToXml());
        var data = doc.Descendants().Where(x => x.Name.LocalName == "Data")
            .Select(x => (Name: (string?)x.Attribute("Name"), x.Value))
            .Where(p => p.Name is not null)
            .GroupBy(p => p.Name!)
            .ToDictionary(g => g.Key, g => g.First().Value);

        string? Get(string key) => data.TryGetValue(key, out var v) ? v : null;
        long? Num(string key) =>
            long.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        var name = Get("FriendlyName");
        string? fileName = null;
        if (string.IsNullOrWhiteSpace(name)) name = Get("Name");
        else if (!string.IsNullOrWhiteSpace(Get("Name"))) fileName = Get("Name");
        return new BootEvent(rec.Id, time.ToUniversalTime(), string.IsNullOrWhiteSpace(name) ? null : name,
            Num("BootTime"), Num("DegradationTime"), fileName);
    }
}
