using System.Text.Json;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Moves old files of a folder (or one single file) into the session's Ablage. Nothing is deleted;
/// the original paths are kept in a sidecar file so Undo can move everything back.
/// </summary>
public sealed class MoveToAblageChange : IChange
{
    public static readonly TimeSpan DefaultMinAge = TimeSpan.FromDays(2);

    sealed record Entry(string Original, string Stored);

    readonly IFileOps _files;
    readonly string _path;
    readonly TimeSpan _minAge;
    readonly bool _single;

    public MoveToAblageChange(IFileOps f, string folder, TimeSpan minAge)
    {
        _files = f; _path = folder; _minAge = minAge;
    }

    MoveToAblageChange(IFileOps f, string file) { _files = f; _path = file; _single = true; }

    /// <summary>Single-file variant: no age filter.</summary>
    public static MoveToAblageChange ForFile(IFileOps f, string path) => new(f, path);

    public static MoveToAblageChange FromRecord(ChangeRecord r, IFileOps f) =>
        r.Kind == "file" ? new(f, r.Target) : new(f, r.Target, DefaultMinAge);

    public string Kind => _single ? "file" : "temp";
    public string Target => _path;
    public string Description => _single
        ? $"Datei „{Path.GetFileName(_path)}“ in die Ablage verschieben"
        : $"Temporäre Dateien in „{_path}“ in die Ablage verschieben";

    // Lazy enumeration may fail midway (folder vanished, access denied): keep what we got.
    internal static (List<FileInfo> Files, bool Complete) Collect(IFileOps f, string folder, TimeSpan minAge)
    {
        var list = new List<FileInfo>();
        try
        {
            foreach (var fi in f.EnumerateOlderThan(folder, minAge)) list.Add(fi);
            return (list, true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            FileLog.Warn($"Auflisten von „{folder}“ unvollständig", ex);
            return (list, false);
        }
    }

    public ChangeRecord Apply(SessionContext ctx)
    {
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(ctx.AblageDir, id);
        var entries = new List<Entry>();
        int skipped = 0;
        long bytes = 0;

        List<FileInfo> candidates;
        if (_single)
        {
            if (!File.Exists(_path)) throw new InvalidOperationException($"Datei „{_path}“ existiert nicht mehr.");
            candidates = [new FileInfo(_path)];
        }
        else candidates = Collect(_files, _path, _minAge).Files;

        var folder = _single ? Path.GetDirectoryName(Path.GetFullPath(_path))! : Path.GetFullPath(_path);
        foreach (var fi in candidates)
        {
            try
            {
                var size = fi.Length;
                var rel = Path.GetRelativePath(folder, fi.FullName);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { skipped++; continue; }
                var stored = Path.Combine(root, rel);
                _files.Move(fi.FullName, stored);
                entries.Add(new Entry(fi.FullName, stored));
                bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                skipped++;   // locked, denied or rejected path
                FileLog.Warn($"Übersprungen: {fi.FullName}", ex);
            }
        }

        var sidecar = Path.Combine(ctx.SessionDir, $"ablage_{id}.json");
        Directory.CreateDirectory(ctx.SessionDir);
        File.WriteAllText(sidecar, JsonSerializer.Serialize(entries.Select(e => new { original = e.Original, stored = e.Stored })));

        return new ChangeRecord(id, Kind, Target, Description,
            new() { ["path"] = _path },
            new()
            {
                ["moved"] = entries.Count.ToString(),
                ["skipped"] = skipped.ToString(),
                ["bytes"] = bytes.ToString(),
                ["sidecar"] = sidecar,
                ["ablage"] = root,
            },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var sidecar = record.After.GetValueOrDefault("sidecar");
        if (string.IsNullOrWhiteSpace(sidecar) || !File.Exists(sidecar))
            throw new InvalidOperationException("Die Liste der verschobenen Dateien fehlt, Wiederherstellung nicht möglich.");

        var entries = new List<Entry>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(sidecar)))
            foreach (var e in doc.RootElement.EnumerateArray())
                entries.Add(new Entry(e.GetProperty("original").GetString()!, e.GetProperty("stored").GetString()!));

        int restored = 0, conflicts = 0, missing = 0;
        foreach (var e in entries)
        {
            if (_files.Exists(e.Original))
            {
                conflicts++;
                FileLog.Warn($"Nicht wiederhergestellt, Zielpfad existiert wieder: {e.Original} (bleibt in der Ablage: {e.Stored})");
                continue;
            }
            if (!_files.Exists(e.Stored)) { missing++; continue; }
            try { _files.Move(e.Stored, e.Original); restored++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conflicts++;
                FileLog.Warn($"Wiederherstellung fehlgeschlagen: {e.Original}", ex);
            }
        }

        if (restored == 0 && (conflicts > 0 || missing > 0))
            throw new InvalidOperationException(conflicts > 0
                ? $"Keine Datei wiederhergestellt: {conflicts} Zielpfade existieren wieder oder sind gesperrt. Die Dateien liegen weiter in der Ablage."
                : "Keine Datei wiederhergestellt: Die Ablage wurde bereits geleert.");
        if (conflicts > 0)
            FileLog.Warn($"{conflicts} Dateien nicht wiederhergestellt (Konflikt), {restored} wiederhergestellt.");
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var dir = record.After.GetValueOrDefault("ablage");
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? DriftState.Unchanged : DriftState.Gone;
    }
}
