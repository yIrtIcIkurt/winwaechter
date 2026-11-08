using System.Globalization;

namespace WinWaechter.Core.Journal;

public sealed class SessionStore
{
    readonly string _sitzungen;

    // root = %ProgramData%\WinWaechter
    public SessionStore(string root) => _sitzungen = Path.Combine(root, "Sitzungen");

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
        var ctx = new SessionContext(dir);
        Directory.CreateDirectory(ctx.BackupDir);
        Directory.CreateDirectory(ctx.AblageDir);
        return SessionJournal.CreateNew(title, now, ctx);
    }

    // Newest first; folders without a readable journal are skipped.
    public IReadOnlyList<SessionJournal> List()
    {
        if (!Directory.Exists(_sitzungen)) return Array.Empty<SessionJournal>();
        return Directory.GetDirectories(_sitzungen)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Select(SessionJournal.Load)
            .OfType<SessionJournal>()
            .ToList();
    }
}
