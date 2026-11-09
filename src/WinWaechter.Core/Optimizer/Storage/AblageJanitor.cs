using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// What „Ablage jetzt leeren“ would delete, how many folders it keeps, and how many whole program
/// folders (Kind "folder", never auto-purged) it would delete.
/// </summary>
public sealed record AblagePreview(long PurgeableBytes, int KeptFolders, int ProgramFolders);

/// <summary>
/// The only place where Ablage files are really deleted. Auto-purge per kind: "cache" after
/// CacheMaxAge (1 day), "temp"/"file" after the setting (default 14 days), "folder" never (only PurgeNow).
/// Only Ablage folders of records that are Applied or Undone are purged. A folder of a Pending or
/// Failed record (crash or error midway) or one no record names is kept, so files whose move was
/// never confirmed are never deleted silently.
/// </summary>
public sealed class AblageJanitor
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(1);

    static readonly string[] AblageKinds = ["temp", "file", "cache", "folder"];

    static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    readonly SessionStore _store;

    public AblageJanitor(SessionStore store) => _store = store;

    /// <summary>Auto-purge: maxAge applies to temp/file; cache uses CacheMaxAge; folder is never purged here.</summary>
    public long Purge(TimeSpan maxAge) => Run((age, owners) =>
        !owners.Any(r => r.Kind == "folder")
        && age > (owners.Any(r => r.Kind == "cache") ? CacheMaxAge : maxAge));

    public long PurgeNow() => Run((_, _) => true);

    /// <summary>
    /// What PurgeNow would do, without deleting: bytes it would free and the number of non-empty
    /// folders it keeps (unfinished or unknown moves). Links are never followed.
    /// </summary>
    public AblagePreview PreviewNow()
    {
        long bytes = 0;
        int kept = 0, programs = 0;
        foreach (var (_, sub, owners, finished, _) in Classify())
        {
            try
            {
                if (finished)
                {
                    bytes += Size(sub);
                    if (owners.Any(r => r.Kind == "folder") && HasEntries(sub)) programs++;
                }
                else if (HasFiles(sub)) kept++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn($"Ablage „{sub.FullName}“ nicht lesbar", ex);
            }
        }
        return new AblagePreview(bytes, kept, programs);
    }

    // due(session age, owners) says whether a subfolder is up for purging; unfinished ones are kept anyway.
    long Run(Func<TimeSpan, List<ChangeRecord>, bool> due)
    {
        long freed = 0;
        foreach (var (j, sub, owners, finished, age) in Classify())
        {
            try
            {
                if (!due(age, owners)) continue;
                if (!finished)
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

    // Every Ablage subfolder of every session with the records naming it (and their kinds) and the
    // session age; finished = it has owners and all of them are Applied/Undone.
    // Reparse-point subfolders are skipped entirely.
    IEnumerable<(SessionJournal J, DirectoryInfo Sub, List<ChangeRecord> Owners, bool Finished, TimeSpan Age)> Classify()
    {
        foreach (var j in _store.List())
        {
            var dir = j.Context.AblageDir;
            if (!Directory.Exists(dir)) continue;
            var age = DateTimeOffset.Now - j.Created;

            var byFolder = new Dictionary<string, List<ChangeRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in j.Records.Where(r => AblageKinds.Contains(r.Kind)))
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
                var finished = known && owners!.All(r => r.Status is ChangeStatus.Applied or ChangeStatus.Undone);
                yield return (j, sub, owners ?? [], finished, age);
            }
        }
    }

    static long Size(DirectoryInfo d) => d.EnumerateFiles("*", NoLinks).Sum(f => f.Length);

    static bool HasFiles(DirectoryInfo d) => d.EnumerateFiles("*", NoLinks).Any();

    static bool HasEntries(DirectoryInfo d) => d.EnumerateFileSystemInfos().Any();

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
