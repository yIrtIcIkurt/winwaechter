using System.Text;
using System.Text.Json;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Moves old files of a folder (or one single file, or one whole folder) into the session's Ablage.
/// Nothing is deleted; the original paths are kept in a sidecar file so Undo can move everything back.
/// The sidecar is appended after every single move, so a crash midway still leaves a complete list.
/// Kinds: "temp" (one folder, age filter), "file" (one file), "cache" (include folders below a root,
/// or one file) and "folder" (a whole folder in one MoveDirectory).
/// </summary>
public sealed class MoveToAblageChange : IChange
{
    public static readonly TimeSpan DefaultMinAge = TimeSpan.FromDays(2);

    sealed record Entry(string Original, string Stored);

    readonly IFileOps _files;
    readonly string _kind;
    readonly string _path;
    readonly IReadOnlyList<string> _includes;
    readonly TimeSpan _minAge;
    readonly bool _ageByNewer;
    readonly bool _single;
    string? _plannedId;

    public MoveToAblageChange(IFileOps f, string folder, TimeSpan minAge)
        : this(f, "temp", folder, [folder], minAge, false, false) { }

    MoveToAblageChange(IFileOps f, string kind, string path, IReadOnlyList<string> includes, TimeSpan minAge,
        bool ageByNewer, bool single)
    {
        _files = f; _kind = kind; _path = path; _includes = includes;
        _minAge = minAge; _ageByNewer = ageByNewer; _single = single;
    }

    /// <summary>Single-file variant: no age filter.</summary>
    public static MoveToAblageChange ForFile(IFileOps f, string path) => new(f, "file", path, [], TimeSpan.Zero, false, true);

    /// <summary>
    /// Cache variant: old files of every include folder, stored relative to root. Every include must be
    /// root itself or below it. With ageByNewerTimestamp a file is old only when both its write and its
    /// creation time are older than minAge.
    /// </summary>
    public static MoveToAblageChange ForCache(IFileOps f, string root, IReadOnlyList<string> includeDirs, TimeSpan minAge,
        bool ageByNewerTimestamp = false)
    {
        if (includeDirs.Count == 0) throw new ArgumentException("Mindestens ein Cache-Ordner ist nötig.", nameof(includeDirs));
        var r = Path.GetFullPath(root).TrimEnd('\\');
        foreach (var d in includeDirs)
        {
            var full = Path.GetFullPath(d).TrimEnd('\\');
            if (!full.Equals(r, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Cache-Ordner „{d}“ liegt nicht unter „{root}“.", nameof(includeDirs));
        }
        return new(f, "cache", root, includeDirs.ToList(), minAge, ageByNewerTimestamp, false);
    }

    /// <summary>Cache variant for one single file (e.g. a memory dump), no age filter.</summary>
    public static MoveToAblageChange ForCacheFile(IFileOps f, string path) => new(f, "cache", path, [], TimeSpan.Zero, false, true);

    /// <summary>A whole folder in one MoveDirectory (uninstall leftovers).</summary>
    public static MoveToAblageChange ForFolder(IFileOps f, string folder) => new(f, "folder", folder, [], TimeSpan.Zero, false, false);

    // Undo only needs Before["path"] and the sidecar, so the defaults stay compatible with 0.1 journals.
    public static MoveToAblageChange FromRecord(ChangeRecord r, IFileOps f) => r.Kind switch
    {
        "file" => ForFile(f, r.Target),
        "cache" => r.Before.GetValueOrDefault("single") == "true"
            ? ForCacheFile(f, r.Target)
            : ForCache(f, r.Target, [r.Target], TimeSpan.Zero),
        "folder" => ForFolder(f, r.Target),
        _ => new(f, r.Target, DefaultMinAge),
    };

    bool IsFolderKind => _kind == "folder";

    public string Kind => _kind;
    public string Target => _path;
    public string Description => _kind switch
    {
        "folder" => $"Ordner „{FolderName}“ in die Ablage verschieben",
        _ when _single => $"Datei „{Path.GetFileName(_path)}“ in die Ablage verschieben",
        "cache" => $"Zwischenspeicher in „{_path}“ in die Ablage verschieben",
        _ => $"Temporäre Dateien in „{_path}“ in die Ablage verschieben",
    };

    string FolderName => Path.GetFileName(Path.GetFullPath(_path).TrimEnd('\\'));

    // Folder that bounds every original path (checked for junctions before each move).
    string SourceRoot => _single ? Path.GetDirectoryName(Path.GetFullPath(_path))! : Path.GetFullPath(_path);

    static Dictionary<string, string?> PathsFor(SessionContext ctx, string id) => new()
    {
        ["sidecar"] = Path.Combine(ctx.SessionDir, $"ablage_{id}.json"),
        ["ablage"] = Path.Combine(ctx.AblageDir, id),
    };

    Dictionary<string, string?> BeforeValues()
    {
        var b = new Dictionary<string, string?> { ["path"] = _path };
        if (_kind == "cache")
        {
            if (_single) b["single"] = "true";
            else b["include"] = string.Join('|', _includes);
        }
        return b;
    }

    /// <summary>Sidecar and Ablage folder go into the Pending record before the first file moves.</summary>
    public ChangeRecord Plan(SessionContext ctx)
    {
        _plannedId = Guid.NewGuid().ToString("N");
        return new ChangeRecord(_plannedId, Kind, Target, Description, BeforeValues(),
            PathsFor(ctx, _plannedId), ChangeStatus.Pending, DateTimeOffset.Now);
    }

    /// <summary>What Apply would move right now (files and bytes); nothing is moved.</summary>
    public (int Files, long Bytes) Measure()
    {
        if (IsFolderKind)
            return _files.InspectFolder(_path) is { } facts ? (facts.Files, facts.Bytes) : (0, 0L);
        if (_single)
            return File.Exists(_path) ? (1, new FileInfo(_path).Length) : (0, 0L);
        var files = Candidates();
        long bytes = 0;
        foreach (var fi in files)
        {
            try { bytes += fi.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* vanished meanwhile */ }
        }
        return (files.Count, bytes);
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

    // Old files of every include folder, each path once; optionally aged by the newer timestamp.
    List<FileInfo> Candidates()
    {
        var cutoff = DateTime.UtcNow - _minAge;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<FileInfo>();
        foreach (var dir in _includes)
            foreach (var fi in Collect(_files, dir, _minAge).Files)
            {
                if (_ageByNewer && fi.CreationTimeUtc >= cutoff) continue;
                if (seen.Add(fi.FullName)) list.Add(fi);
            }
        return list;
    }

    public ChangeRecord Apply(SessionContext ctx)
    {
        var id = _plannedId ?? Guid.NewGuid().ToString("N");
        _plannedId = null;
        var paths = PathsFor(ctx, id);
        if (IsFolderKind) return ApplyFolder(ctx, id, paths);
        var root = paths["ablage"]!;
        var sidecar = paths["sidecar"]!;
        int moved = 0, skipped = 0;
        long bytes = 0;

        List<FileInfo> candidates;
        if (_single)
        {
            if (!File.Exists(_path))
            {
                if (_kind == "cache") return VanishedFile(id, paths);   // engine goes on (ruling R7)
                throw new InvalidOperationException($"Datei „{_path}“ existiert nicht mehr.");
            }
            candidates = [new FileInfo(_path)];
        }
        else candidates = Candidates();

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
            BeforeValues(),
            new(paths)
            {
                ["moved"] = moved.ToString(),
                ["skipped"] = skipped.ToString(),
                ["bytes"] = bytes.ToString(),
            },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    // A single cache file that is gone by the time Apply runs: nothing to move, no sidecar, no Ablage folder.
    ChangeRecord VanishedFile(string id, Dictionary<string, string?> paths)
    {
        var reason = $"Datei „{Path.GetFileName(_path)}“ existiert nicht mehr. Es wurde nichts verschoben.";
        FileLog.Warn(reason);
        var after = new Dictionary<string, string?>(paths)
        {
            ["moved"] = "0", ["skipped"] = "0", ["bytes"] = "0", ["reason"] = reason,
        };
        return new ChangeRecord(id, Kind, Target, Description, BeforeValues(), after, ChangeStatus.Applied, DateTimeOffset.Now);
    }

    // Removes the empty sidecar and the empty Ablage folder a refused move leaves behind; nothing else.
    static void RemoveEmptyLeftovers(string sidecar, string ablageRoot)
    {
        try
        {
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length == 0) File.Delete(sidecar);
            if (Directory.Exists(ablageRoot) && !Directory.EnumerateFileSystemEntries(ablageRoot).Any()) Directory.Delete(ablageRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Warn("Leere Reste der Ablage nicht entfernt", ex);
        }
    }

    // One MoveDirectory of the whole folder. A refused or failed move does not throw (ruling R7):
    // the record says moved = "0" plus a German reason, so the engine goes on with the other changes.
    ChangeRecord ApplyFolder(SessionContext ctx, string id, Dictionary<string, string?> paths)
    {
        var folder = Path.GetFullPath(_path).TrimEnd('\\');
        var name = FolderName;
        var parent = Path.GetDirectoryName(folder);
        var root = paths["ablage"]!;
        var sidecar = paths["sidecar"]!;
        var stored = Path.Combine(root, name);
        Directory.CreateDirectory(ctx.SessionDir);
        File.WriteAllBytes(sidecar, []);
        ctx.ClaimFile(sidecar);   // owner fixed before anything is listed in it

        ChangeRecord Result(bool moved, long bytes, string? reason)
        {
            var after = new Dictionary<string, string?>(paths)
            {
                ["moved"] = moved ? "1" : "0",
                ["skipped"] = moved ? "0" : "1",
                ["bytes"] = bytes.ToString(),
            };
            if (reason is not null) after["reason"] = reason;
            return new ChangeRecord(id, Kind, Target, Description, BeforeValues(), after, ChangeStatus.Applied, DateTimeOffset.Now);
        }

        ChangeRecord Refused(string why)
        {
            var reason = $"Ordner „{name}“ konnte nicht verschoben werden ({why}). Es wurde nichts verschoben.";
            FileLog.Warn(reason);
            RemoveEmptyLeftovers(sidecar, root);
            return Result(false, 0, reason);
        }

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return Refused("Laufwerk statt Ordner");
        if (_files.HasReparsePointInChain(folder, parent)) return Refused("Verknüpfung (Junction) im Pfad");
        if (_files.InspectFolder(folder) is not { } facts) return Refused("Ordner fehlt, ist eine Verknüpfung oder ist nicht lesbar");
        Directory.CreateDirectory(root);
        if (_files.HasReparsePointInChain(stored, ctx.AblageDir)) return Refused("Verknüpfung (Junction) in der Ablage");

        // listed before the move, so a crash right after it still leaves the way back
        using (var log = new FileStream(sidecar, FileMode.Append, FileAccess.Write, FileShare.Read))
            AppendEntry(log, new Entry(folder, stored));
        try
        {
            _files.MoveDirectory(folder, stored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            File.WriteAllBytes(sidecar, []);   // nothing moved, nothing to restore
            FileLog.Warn($"Ordner nicht verschoben: {folder}", ex);
            return Refused(ex.Message);
        }
        return Result(true, facts.Bytes, null);
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

    // The sidecar must exist, lie in this session and come from WinWächter.
    static List<Entry> ReadTrustedSidecar(ChangeRecord record, SessionContext ctx)
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
        return ReadSidecar(sidecar);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var entries = ReadTrustedSidecar(record, ctx);
        if (IsFolderKind) { UndoFolder(record, ctx, entries); return; }

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

    // The folder goes back in one move, only to the recorded path and only while that path is free.
    void UndoFolder(ChangeRecord record, SessionContext ctx, List<Entry> entries)
    {
        var name = FolderName;
        var recorded = Path.GetFullPath(record.Before.GetValueOrDefault("path") ?? _path).TrimEnd('\\');
        var parent = Path.GetDirectoryName(recorded) ?? recorded;
        foreach (var e in entries)
        {
            var original = Path.GetFullPath(e.Original).TrimEnd('\\');
            if (!original.Equals(recorded, StringComparison.OrdinalIgnoreCase)
                || _files.HasReparsePointInChain(original, parent) || _files.HasReparsePointInChain(e.Stored, ctx.AblageDir))
                throw new InvalidOperationException(
                    $"Ordner „{name}“ nicht wiederhergestellt: Der Weg zurück führt über eine Verknüpfung (Junction) oder passt nicht zur Änderung. Er bleibt in der Ablage: {e.Stored}");
            var inAblage = _files.Exists(e.Stored);
            if (_files.Exists(original))
            {
                if (!inAblage) continue;   // crash before the move: the folder never left
                throw new InvalidOperationException($"Ordner „{name}“ existiert wieder am Originalort. Er bleibt in der Ablage: {e.Stored}");
            }
            if (!inAblage)
                throw new InvalidOperationException($"Ordner „{name}“ nicht wiederhergestellt: Die Ablage wurde bereits geleert.");
            _files.MoveDirectory(e.Stored, original);
        }
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var dir = record.After.GetValueOrDefault("ablage");
        return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? DriftState.Unchanged : DriftState.Gone;
    }
}
