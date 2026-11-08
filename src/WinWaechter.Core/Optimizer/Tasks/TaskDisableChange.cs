using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Tasks;

/// <summary>Disables a scheduled task after saving its XML definition.</summary>
public sealed class TaskDisableChange : IChange
{
    readonly ITaskStore _store;
    readonly string _path;

    public TaskDisableChange(ITaskStore store, string path)
    {
        _store = store; _path = path;
    }

    public string Kind => "task";
    public string Target => _path;
    public string Description => DescriptionFor(_store.Get(_path)?.Name);

    string DescriptionFor(string? name) =>
        $"Aufgabe „{(string.IsNullOrWhiteSpace(name) ? _path[(_path.LastIndexOf('\\') + 1)..] : name)}“ deaktivieren";

    public static TaskDisableChange FromRecord(ChangeRecord r, ITaskStore store) => new(store, r.Target);

    public ChangeRecord Apply(SessionContext ctx)
    {
        var live = _store.Get(_path) ?? throw new InvalidOperationException($"Aufgabe „{_path}“ existiert nicht mehr.");
        var xml = _store.ExportXml(_path);

        // Backup first, then disable.
        Directory.CreateDirectory(ctx.BackupDir);
        var file = Path.Combine(ctx.BackupDir, $"task_{SafeFileName(_path)}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);   // UTF-16 LE with BOM, as the XML declares

        _store.SetEnabled(_path, false);

        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, DescriptionFor(live.Name),
            new() { ["xml"] = file, ["enabled"] = "true" },
            new() { ["enabled"] = "false" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (_store.Get(_path) is null)
        {
            var file = record.Before.GetValueOrDefault("xml");
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                throw new InvalidOperationException($"Aufgabe „{_path}“ existiert nicht mehr und die Sicherung fehlt.");
            var xml = File.ReadAllText(file);   // BOM detection
            try { _store.RegisterXml(_path, xml); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                var needsPassword = xml.Contains("<LogonType>Password</LogonType>", StringComparison.OrdinalIgnoreCase);
                throw new InvalidOperationException(needsPassword
                    ? $"Aufgabe konnte nicht neu angelegt werden (gespeichertes Kennwort nötig). Sicherung: {file}"
                    : $"Aufgabe konnte nicht neu angelegt werden: {ex.Message}. Sicherung: {file}", ex);
            }
        }
        // The task was enabled before Apply; make sure it is now.
        if (_store.Get(_path) is { Enabled: false }) _store.SetEnabled(_path, true);
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var live = _store.Get(_path);
        if (live is null) return DriftState.Gone;
        return live.Enabled ? DriftState.Changed : DriftState.Unchanged;
    }

    // Readable name plus a hash of the full path so different paths never collide.
    static string SafeFileName(string s)
    {
        var clean = string.Concat(s.Select(c => c == '\\' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
        return $"{clean}_{hash}";
    }
}
