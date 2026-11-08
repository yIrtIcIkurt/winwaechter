using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Changes;

// Maps a record's Kind to the code that can rebuild its IChange from disk.
public sealed class ChangeFactory : IChangeFactory
{
    readonly Dictionary<string, Func<ChangeRecord, IChange>> _map = new(StringComparer.Ordinal);

    public void Register(string kind, Func<ChangeRecord, IChange> factory) => _map[kind] = factory;

    public IChange Rehydrate(ChangeRecord r) =>
        _map.TryGetValue(r.Kind, out var f)
            ? f(r)
            : throw new InvalidOperationException($"Unbekannte Änderungsart \"{r.Kind}\" – kann nicht rückgängig gemacht werden.");

    public static ChangeFactory CreateDefault(IRegistry reg, IServiceControl sc, ITaskStore ts, IFileOps fo)
    {
        var f = new ChangeFactory();
        f.Register("autostart", r => StartupApprovedChange.FromRecord(r, reg));
        f.Register("service", r => ServiceStartModeChange.FromRecord(r, sc, reg));
        f.Register("task", r => TaskDisableChange.FromRecord(r, ts));
        f.Register("temp", r => MoveToAblageChange.FromRecord(r, fo));
        f.Register("file", r => MoveToAblageChange.FromRecord(r, fo));
        return f;
    }
}
