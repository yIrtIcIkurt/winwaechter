using System.Globalization;

namespace WinWaechter.Core.Journal;

public sealed class SessionStore
{
    readonly string _sitzungen;
    readonly Func<string, bool>? _isTrustedFile;
    readonly Action<string>? _claimFile;

    // root = %ProgramData%\WinWaechter. isTrustedFile (elevated only): journals and sidecars that
    // fail it are ignored, so files planted by a normal user never steer an undo.
    // claimFile (elevated only): sets the owner of every journal/sidecar the app writes.
    public SessionStore(string root, Func<string, bool>? isTrustedFile = null, Action<string>? claimFile = null)
    {
        _claimFile = claimFile;
        _sitzungen = Path.Combine(root, "Sitzungen");
        _isTrustedFile = isTrustedFile;
    }

    // Folder name: yyyy-MM-dd_HHmmss, then _2, _3 ... if taken.
    public SessionJournal Create(string title)
    {
        Directory.CreateDirectory(_sitzungen);
        var now = DateTimeOffset.Now;
        var baseName = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var name = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(_sitzungen, name)); n++)
            name = $"{baseName}_{n}";
        var dir = Path.Combine(_sitzungen, name);
        var ctx = new SessionContext(dir, _isTrustedFile, _claimFile);
        Directory.CreateDirectory(ctx.BackupDir);
        Directory.CreateDirectory(ctx.AblageDir);
        return SessionJournal.CreateNew(title, now, ctx);
    }

    // Newest first; folders without a readable, trusted journal (and junctions) are skipped.
    public IReadOnlyList<SessionJournal> List()
    {
        if (!Directory.Exists(_sitzungen)) return Array.Empty<SessionJournal>();
        return new DirectoryInfo(_sitzungen).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(d => d.FullName)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(d => SessionJournal.Load(d, _isTrustedFile, _claimFile))
            .OfType<SessionJournal>()
            .ToList();
    }
}
