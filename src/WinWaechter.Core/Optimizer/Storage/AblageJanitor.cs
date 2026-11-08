using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// The only place where Ablage files are really deleted (auto-purge after 14 days).
/// Only Ablage folders of records that are Applied or Undone are purged. A folder of a Pending or
/// Failed record (crash or error midway) or one no record names is kept, so files whose move was
/// never confirmed are never deleted silently.
/// </summary>
public sealed class AblageJanitor
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

    static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    readonly SessionStore _store;

    public AblageJanitor(SessionStore store) => _store = store;

    public long Purge(TimeSpan maxAge) => Run(j => j.Created < DateTimeOffset.Now - maxAge);

    public long PurgeNow() => Run(_ => true);

    long Run(Func<SessionJournal, bool> select)
    {
        long freed = 0;
        foreach (var j in _store.List().Where(select))
        {
            var dir = j.Context.AblageDir;
            if (!Directory.Exists(dir)) continue;

            var byFolder = new Dictionary<string, List<ChangeRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in j.Records.Where(r => r.Kind is "temp" or "file"))
            {
                var a = r.After.GetValueOrDefault("ablage");
                if (string.IsNullOrWhiteSpace(a)) continue;
                var key = Path.GetFullPath(a).TrimEnd('\\');
                if (!byFolder.TryGetValue(key, out var list)) byFolder[key] = list = [];
                list.Add(r);
            }

            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                try
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        FileLog.Warn($"Ablage-Ordner ist eine Verknüpfung, nicht geleert: {sub.FullName}");
                        continue;
                    }
                    if (!byFolder.TryGetValue(sub.FullName.TrimEnd('\\'), out var owners)
                        || owners.Any(r => r.Status is not (ChangeStatus.Applied or ChangeStatus.Undone)))
                    {
                        if (HasFiles(sub))
                            FileLog.Warn($"Ablage-Ordner behalten, Änderung nicht abgeschlossen oder ohne Eintrag: {sub.FullName}");
                        continue;
                    }
                    freed += Delete(sub);
                    foreach (var r in owners)
                        j.Upsert(r with { After = new Dictionary<string, string?>(r.After) { ["purged"] = "true" } });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLog.Warn($"Ablage „{sub.FullName}“ konnte nicht geleert werden", ex);
                }
            }
        }
        return freed;
    }

    static bool HasFiles(DirectoryInfo d) => d.EnumerateFiles("*", NoLinks).Any();

    static long Delete(DirectoryInfo dir)
    {
        long bytes = 0;
        foreach (var f in dir.EnumerateFiles("*", NoLinks).ToList())
        {
            bytes += f.Length;
            try { f.Attributes = FileAttributes.Normal; } catch { }   // read-only files must not block the purge
        }
        dir.Delete(true);   // removes links inside without following them
        return bytes;
    }
}
