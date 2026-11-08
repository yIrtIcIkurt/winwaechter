using System.Text;
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
/// The sidecar is appended after every single move, so a crash midway still leaves a complete list.
/// </summary>
public sealed class MoveToAblageChange : IChange
{
    public static readonly TimeSpan DefaultMinAge = TimeSpan.FromDays(2);

    sealed record Entry(string Original, string Stored);

    readonly IFileOps _files;
    readonly string _path;
    readonly TimeSpan _minAge;
    readonly bool _single;
    string? _plannedId;

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

    // Folder that bounds every original path (checked for junctions before each move).
    string SourceRoot => _single ? Path.GetDirectoryName(Path.GetFullPath(_path))! : Path.GetFullPath(_path);

    static Dictionary<string, string?> PathsFor(SessionContext ctx, string id) => new()
    {
        ["sidecar"] = Path.Combine(ctx.SessionDir, $"ablage_{id}.json"),
        ["ablage"] = Path.Combine(ctx.AblageDir, id),
    };

    /// <summary>Sidecar and Ablage folder go into the Pending record before the first file moves.</summary>
    public ChangeRecord Plan(SessionContext ctx)
    {
        _plannedId = Guid.NewGuid().ToString("N");
        return new ChangeRecord(_plannedId, Kind, Target, Description, new() { ["path"] = _path },
            PathsFor(ctx, _plannedId), ChangeStatus.Pending, DateTimeOffset.Now);
    }

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
        var id = _plannedId ?? Guid.NewGuid().ToString("N");
        _plannedId = null;
        var paths = PathsFor(ctx, id);
        var root = paths["ablage"]!;
        var sidecar = paths["sidecar"]!;
        int moved = 0, skipped = 0;
        long bytes = 0;

        List<FileInfo> candidates;
        if (_single)
        {
            if (!File.Exists(_path)) throw new InvalidOperationException($"Datei „{_path}“ existiert nicht mehr.");
            candidates = [new FileInfo(_path)];
        }
        else candidates = Collect(_files, _path, _minAge).Files;

        Directory.CreateDirectory(ctx.SessionDir);
        Directory.CreateDirectory(root);
        var folder = SourceRoot;
        File.WriteAllBytes(sidecar, []);
        ctx.ClaimFile(sidecar);   // owner fixed before anything is listed in it
        using (var log = new FileStream(sidecar, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            foreach (var fi in candidates)
            {
                try
                {
                    var size = fi.Length;
                    var rel = Path.GetRelativePath(folder, fi.FullName);
                    if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) { skipped++; continue; }
                    var stored = Path.Combine(root, rel);
                    if (_files.HasReparsePointInChain(fi.FullName, folder) || _files.HasReparsePointInChain(stored, ctx.AblageDir))
                    {
                        skipped++;
                        FileLog.Warn($"Übersprungen, Verknüpfung (Junction) im Pfad: {fi.FullName}");
                        continue;
                    }
                    _files.Move(fi.FullName, stored);
                    AppendEntry(log, new Entry(fi.FullName, stored));
                    moved++;
                    bytes += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    skipped++;   // locked, denied or rejected path
                    FileLog.Warn($"Übersprungen: {fi.FullName}", ex);
                }
            }
        }

        return new ChangeRecord(id, Kind, Target, Description,
            new() { ["path"] = _path },
            new(paths)
            {
                ["moved"] = moved.ToString(),
                ["skipped"] = skipped.ToString(),
                ["bytes"] = bytes.ToString(),
            },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    // One JSON object per line, flushed right away so the list survives a crash of the app.
    static void AppendEntry(FileStream log, Entry e)
    {
        var line = JsonSerializer.Serialize(new { original = e.Original, stored = e.Stored }) + "\n";
        log.Write(Encoding.UTF8.GetBytes(line));
        log.Flush();
    }

    // Accepts the JSON-lines format and the older single JSON array.
    static List<Entry> ReadSidecar(string path)
    {
        var text = File.ReadAllText(path);
        var entries = new List<Entry>();
        static Entry ToEntry(JsonElement e) =>
            new(e.GetProperty("original").GetString()!, e.GetProperty("stored").GetString()!);

        if (text.TrimStart().StartsWith('['))
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var e in doc.RootElement.EnumerateArray()) entries.Add(ToEntry(e));
            return entries;
        }
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                entries.Add(ToEntry(doc.RootElement));
            }
            catch (JsonException) { /* a line cut off by a crash */ }
        }
        return entries;
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var sidecar = record.After.GetValueOrDefault("sidecar");
        if (string.IsNullOrWhiteSpace(sidecar) || !File.Exists(sidecar))
            throw new InvalidOperationException("Die Liste der verschobenen Dateien fehlt, Wiederherstellung nicht möglich.");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(sidecar)), Path.GetFullPath(ctx.SessionDir).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Liste der verschobenen Dateien liegt außerhalb der Sitzung, Wiederherstellung abgelehnt.");
        if (!ctx.IsTrustedFile(sidecar))
        {
            FileLog.Warn($"Liste der verschobenen Dateien ignoriert, fremder Besitzer: {sidecar}");
            throw new InvalidOperationException("Die Liste der verschobenen Dateien stammt nicht von WinWächter, Wiederherstellung abgelehnt.");
        }

        var entries = ReadSidecar(sidecar);
        var recorded = record.Before.GetValueOrDefault("path");
        var folderRoot = !_single && !string.IsNullOrWhiteSpace(recorded) ? Path.GetFullPath(recorded) : null;

        int restored = 0, conflicts = 0, missing = 0, blocked = 0;
        foreach (var e in entries)
        {
            if (!_files.Exists(e.Stored)) { missing++; continue; }
            var sourceRoot = folderRoot ?? Path.GetDirectoryName(Path.GetFullPath(e.Original))!;
            if (_files.HasReparsePointInChain(e.Original, sourceRoot) || _files.HasReparsePointInChain(e.Stored, ctx.AblageDir))
            {
                blocked++;
                FileLog.Warn($"Nicht wiederhergestellt, Verknüpfung (Junction) im Zielpfad: {e.Original} (bleibt in der Ablage: {e.Stored})");
                continue;
            }
            if (_files.Exists(e.Original))
            {
                conflicts++;
                FileLog.Warn($"Nicht wiederhergestellt, Zielpfad existiert wieder: {e.Original} (bleibt in der Ablage: {e.Stored})");
                continue;
            }
            try { _files.Move(e.Stored, e.Original); restored++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conflicts++;
                FileLog.Warn($"Wiederherstellung fehlgeschlagen: {e.Original}", ex);
            }
        }

        if (restored == 0 && (conflicts > 0 || missing > 0 || blocked > 0))
            throw new InvalidOperationException(
                conflicts > 0 ? $"Keine Datei wiederhergestellt: {conflicts} Zielpfade existieren wieder oder sind gesperrt. Die Dateien liegen weiter in der Ablage."
                : blocked > 0 ? $"Keine Datei wiederhergestellt: {blocked} Zielpfade führen über eine Verknüpfung (Junction). Die Dateien liegen weiter in der Ablage."
                : "Keine Datei wiederhergestellt: Die Ablage wurde bereits geleert.");
        if (conflicts > 0 || blocked > 0)
            FileLog.Warn($"{conflicts} Dateien nicht wiederhergestellt (Konflikt), {blocked} wegen Verknüpfung übersprungen, {restored} wiederhergestellt.");
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var dir = record.After.GetValueOrDefault("ablage");
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? DriftState.Unchanged : DriftState.Gone;
    }
}
