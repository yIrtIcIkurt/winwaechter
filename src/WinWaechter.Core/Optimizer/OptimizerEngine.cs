using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Optimizer;

public sealed record ApplyResult(SessionJournal Session, int Applied, int Failed, RestorePointResult RestorePoint);

public sealed record UndoResult(int Undone, int SkippedDrift, int Failed, IReadOnlyList<string> Messages);

// scan -> apply (journalled) -> undo
public sealed class OptimizerEngine
{
    readonly IReadOnlyList<Func<IReadOnlyList<Finding>>> _scanners;
    readonly SessionStore _store;
    readonly IChangeFactory _factory;
    readonly Func<string, RestorePointResult> _restorePoint;

    public OptimizerEngine(IEnumerable<Func<IReadOnlyList<Finding>>> scanners, SessionStore store,
        IChangeFactory factory, Func<string, RestorePointResult> restorePoint)
    {
        _scanners = scanners.ToList();
        _store = store;
        _factory = factory;
        _restorePoint = restorePoint;
    }

    public Task<IReadOnlyList<Finding>> ScanAsync(IProgress<string>? p, CancellationToken ct) =>
        Task.Run<IReadOnlyList<Finding>>(() =>
        {
            var all = new List<Finding>();
            for (var i = 0; i < _scanners.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                p?.Report($"Prüfe Bereich {i + 1} von {_scanners.Count} …");
                try { all.AddRange(_scanners[i]()); }
                catch (Exception ex) { FileLog.Warn("Scanner fehlgeschlagen", ex); }
            }
            return all;
        }, ct);

    public Task<ApplyResult> ApplyAsync(string title, IReadOnlyList<Finding> selected,
        IProgress<string>? p, CancellationToken ct) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();   // before any restore point or session exists
            var changes = selected.Select(f => f.Change).OfType<IChange>().ToList();
            p?.Report("Wiederherstellungspunkt wird erstellt …");
            var rp = _restorePoint(title);
            var session = _store.Create(title);
            int applied = 0, failed = 0;

            for (var i = 0; i < changes.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var change = changes[i];
                p?.Report($"({i + 1}/{changes.Count}) {change.Description}");
                var id = Guid.NewGuid().ToString("N");
                // journal first, so a crash mid-change leaves a trace
                session.Upsert(new ChangeRecord(id, change.Kind, change.Target, change.Description,
                    new(), new(), ChangeStatus.Pending, DateTimeOffset.Now));
                try
                {
                    var done = change.Apply(session.Context);
                    session.Upsert(done with { Id = id, Status = ChangeStatus.Applied, Error = null });
                    applied++;
                }
                catch (Exception ex)
                {
                    FileLog.Error($"Änderung fehlgeschlagen: {change.Description}", ex);
                    session.Upsert(new ChangeRecord(id, change.Kind, change.Target, change.Description,
                        new(), new(), ChangeStatus.Failed, DateTimeOffset.Now,
                        $"Änderung fehlgeschlagen: {ex.Message}"));
                    failed++;
                    break;
                }
            }
            return new ApplyResult(session, applied, failed, rp);
        }, CancellationToken.None);

    public UndoResult UndoSession(SessionJournal s) =>
        Undo(s, s.Records.Where(r => r.Status == ChangeStatus.Applied).Reverse().ToList());

    public UndoResult UndoOne(SessionJournal s, string recordId) =>
        Undo(s, s.Records.Where(r => r.Id == recordId && r.Status == ChangeStatus.Applied).ToList());

    UndoResult Undo(SessionJournal s, IReadOnlyList<ChangeRecord> records)
    {
        int undone = 0, skipped = 0, failed = 0;
        var msgs = new List<string>();

        void Skip(ChangeRecord r, string msg)
        {
            s.Upsert(r with { Status = ChangeStatus.SkippedDrift, Error = msg });
            msgs.Add(msg);
            skipped++;
        }

        foreach (var r in records)
        {
            try
            {
                if (r.After.TryGetValue("purged", out var purged) && purged == "true")
                {
                    Skip(r, $"{r.Description}: Ablage wurde bereits geleert – nicht wiederherstellbar");
                    continue;
                }
                var change = _factory.Rehydrate(r);
                var drift = change.CheckDrift(r);
                if (drift == DriftState.Changed || (drift == DriftState.Gone && r.Kind != "task"))
                {
                    Skip(r, drift == DriftState.Changed
                        ? $"{r.Description}: seit der Änderung verändert – übersprungen"
                        : $"{r.Description}: nicht mehr vorhanden – übersprungen");
                    continue;
                }
                change.Undo(r, s.Context);
                s.Upsert(r with { Status = ChangeStatus.Undone, Error = null });
                undone++;
            }
            catch (Exception ex)
            {
                FileLog.Error($"Rückgängig fehlgeschlagen: {r.Description}", ex);
                s.Upsert(r with { Error = ex.Message });
                msgs.Add($"{r.Description}: Rückgängig fehlgeschlagen – {ex.Message}");
                failed++;
            }
        }
        return new UndoResult(undone, skipped, failed, msgs);
    }
}
