using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>What „Ablage jetzt leeren“ would delete, and how many folders it keeps.</summary>
public sealed record AblagePreview(long PurgeableBytes, int KeptFolders);

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

    /// <summary>
    /// What PurgeNow would do, without deleting: bytes it would free and the number of non-empty
    /// folders it keeps (unfinished or unknown moves). Links are never followed.
    /// </summary>
    public AblagePreview PreviewNow()
    {
        long bytes = 0;
        var kept = 0;
        foreach (var (_, sub, _, purge) in Classify(_ => true))
        {
            try
            {
                if (purge) bytes += Size(sub);
                else if (HasFiles(sub)) kept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn($"Ablage „{sub.FullName}“ nicht lesbar", ex);
            }
        }
        return new AblagePreview(bytes, kept);
    }

    long Run(Func<SessionJournal, bool> select)
    {
        long freed = 0;
        foreach (var (j, sub, owners, purge) in Classify(select))
        {
            try
            {
                if (!purge)
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
        return freed;
    }

    // Every Ablage subfolder of the selected sessions; purge = all records naming it are Applied/Undone.
    // Reparse-point subfolders are skipped entirely.
    IEnumerable<(SessionJournal J, DirectoryInfo Sub, List<ChangeRecord> Owners, bool Purge)> Classify(Func<SessionJournal, bool> select)
    {
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
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileLog.Warn($"Ablage-Ordner ist eine Verknüpfung, wird nicht angefasst: {sub.FullName}");
                    continue;
                }
                var known = byFolder.TryGetValue(sub.FullName.TrimEnd('\\'), out var owners);
                var purge = known && owners!.All(r => r.Status is ChangeStatus.Applied or ChangeStatus.Undone);
                yield return (j, sub, owners ?? [], purge);
            }
        }
    }

    static long Size(DirectoryInfo d) => d.EnumerateFiles("*", NoLinks).Sum(f => f.Length);

    static bool HasFiles(DirectoryInfo d) => d.EnumerateFiles("*", NoLinks).Any();

    // Depth-first by hand: links are removed as links (never entered), files and folders deleted.
    static long Delete(DirectoryInfo dir)
    {
        long bytes = 0;
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }).ToList())
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (e is DirectoryInfo link) Directory.Delete(link.FullName, recursive: false);
                else File.Delete(e.FullName);
            }
            else if (e is DirectoryInfo sub) bytes += Delete(sub);
            else if (e is FileInfo f)
            {
                bytes += f.Length;
                try { f.Attributes = FileAttributes.Normal; } catch { }   // read-only files must not block the purge
                f.Delete();
            }
        }
        dir.Attributes = FileAttributes.Directory;
        dir.Delete(false);
        return bytes;
    }
}
