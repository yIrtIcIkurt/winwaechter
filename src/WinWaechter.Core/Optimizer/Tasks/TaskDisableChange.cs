using System.Runtime.InteropServices;
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
        File.WriteAllText(file, xml);

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
            try { _store.RegisterXml(_path, File.ReadAllText(file)); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                // Tasks stored with a password (LogonType=Password) cannot be recreated without it.
                throw new InvalidOperationException(
                    $"Aufgabe konnte nicht neu angelegt werden (gespeichertes Kennwort nötig). Sicherung: {file}", ex);
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

    static string SafeFileName(string s) =>
        string.Concat(s.Select(c => c == '\\' || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
