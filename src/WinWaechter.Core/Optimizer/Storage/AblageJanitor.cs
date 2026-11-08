using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>The only place where Ablage files are really deleted (auto-purge after 14 days).</summary>
public sealed class AblageJanitor
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

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
            try
            {
                freed += DeleteContents(dir);
                foreach (var r in j.Records.Where(r => r.Kind is "temp" or "file"))
                    j.Upsert(r with { After = new Dictionary<string, string?>(r.After) { ["purged"] = "true" } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn($"Ablage „{dir}“ konnte nicht geleert werden", ex);
            }
        }
        return freed;
    }

    static long DeleteContents(string dir)
    {
        long bytes = 0;
        var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        foreach (var f in files)
        {
            bytes += f.Length;
            try { f.Attributes = FileAttributes.Normal; } catch { }   // read-only files must not block the purge
        }
        foreach (var sub in Directory.GetDirectories(dir)) Directory.Delete(sub, true);
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        return bytes;
    }
}
