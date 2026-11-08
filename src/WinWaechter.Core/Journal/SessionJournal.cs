using System.Text.Json;
using System.Text.Json.Serialization;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Journal;

public sealed class SessionContext
{
    public SessionContext(string sessionDir, Func<string, bool>? isTrustedFile = null)
    {
        SessionDir = sessionDir;
        BackupDir = Path.Combine(sessionDir, "Sicherung");
        AblageDir = Path.Combine(sessionDir, "Ablage");
        IsTrustedFile = isTrustedFile ?? (_ => true);
    }

    public string SessionDir { get; }
    public string BackupDir { get; }
    public string AblageDir { get; }

    /// <summary>
    /// Whether a file in the session may steer an undo (elevated: owned by Administrators or SYSTEM).
    /// Always true in Dev mode and tests.
    /// </summary>
    public Func<string, bool> IsTrustedFile { get; }
}

public sealed class SessionJournal
{
    internal const string FileName = "journal.json";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    sealed class Doc
    {
        public string Title { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public List<ChangeRecord> Records { get; set; } = new();
    }

    readonly object _lock = new();
    readonly List<ChangeRecord> _records;

    SessionJournal(string title, DateTimeOffset created, SessionContext ctx, List<ChangeRecord> records)
    {
        Title = title;
        Created = created;
        Context = ctx;
        _records = records;
    }

    public string Title { get; }
    public DateTimeOffset Created { get; }
    public SessionContext Context { get; }

    public IReadOnlyList<ChangeRecord> Records
    {
        get { lock (_lock) return _records.ToArray(); }
    }

    string FilePath => Path.Combine(Context.SessionDir, FileName);

    internal static SessionJournal CreateNew(string title, DateTimeOffset created, SessionContext ctx)
    {
        var j = new SessionJournal(title, created, ctx, new List<ChangeRecord>());
        j.Save();
        return j;
    }

    // Returns null when the journal is missing, unreadable or not trusted (a leftover .tmp is ignored).
    internal static SessionJournal? Load(string sessionDir, Func<string, bool>? isTrustedFile = null)
    {
        try
        {
            var path = Path.Combine(sessionDir, FileName);
            if (!File.Exists(path)) return null;
            var ctx = new SessionContext(sessionDir, isTrustedFile);
            if (!ctx.IsTrustedFile(path))
            {
                FileLog.Warn($"Sitzung ignoriert, Journal hat einen fremden Besitzer: {path}");
                return null;
            }
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), Json);
            if (doc is null) return null;
            return new SessionJournal(doc.Title, doc.Created, ctx, doc.Records ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Insert or replace by Id, then write atomically.
    public void Upsert(ChangeRecord r)
    {
        lock (_lock)
        {
            var i = _records.FindIndex(x => x.Id == r.Id);
            if (i >= 0) _records[i] = r; else _records.Add(r);
            Save();
        }
    }

    void Save()
    {
        lock (_lock)
        {
            var doc = new Doc { Title = Title, Created = Created, Records = _records };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }
}
