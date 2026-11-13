using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Optimizer;

public sealed record ApplyResult(SessionJournal Session, int Applied, int Failed, RestorePointResult RestorePoint);

public sealed record UndoResult(int Undone, int SkippedDrift, int Failed, IReadOnlyList<string> Messages);

/// <summary>Undo progress: record Index of Count (1-based, in undo order) and a German line for the UI.</summary>
public sealed record UndoProgress(int Index, int Count, string Text);

/// <summary>A change whose undo restores many files and can say how many are back so far.</summary>
public interface IProgressUndo
{
    void Undo(ChangeRecord record, SessionContext ctx, Action<int> filesRestored);
}

// scan -> apply (journalled) -> undo
public sealed class OptimizerEngine
{
    /// <summary>File-restoring undos report every this many files.</summary>
    public const int FileProgressStep = 500;

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

    /// <summary>
    /// Applies the selected changes in a new session. A failure stops the run, unless continueOnError:
    /// then the failed change is journalled Failed and the rest still runs.
    /// </summary>
    public Task<ApplyResult> ApplyAsync(string title, IReadOnlyList<Finding> selected,
        IProgress<string>? p, CancellationToken ct, bool continueOnError = false) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();   // before any restore point or session exists
            return Apply(title, selected.Select(f => f.Change).OfType<IChange>().ToList(), p, ct, continueOnError);
        }, CancellationToken.None);

    /// <summary>A Failed record that carries retry data (IChange.RetryData).</summary>
    public static bool CanRetry(ChangeRecord r) => r.Status == ChangeStatus.Failed && r.RetryBefore is not null;

    /// <summary>
    /// Rebuilds a failed change from its retry data through the kind's own FromRecord (so its whitelist
    /// check runs) and applies it again in a new session.
    /// </summary>
    public Task<ApplyResult> RetryAsync(ChangeRecord failed, IProgress<string>? p, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (!CanRetry(failed))
                throw new InvalidOperationException("Für diesen Eintrag ist kein erneuter Versuch möglich.");
            var change = _factory.Rehydrate(failed with
            {
                Before = failed.RetryBefore!, After = failed.RetryAfter ?? new(), Status = ChangeStatus.Applied,
            });
            ct.ThrowIfCancellationRequested();
            return Apply($"Erneut versuchen: {failed.Description}", [change], p, ct, continueOnError: false);
        }, CancellationToken.None);

    ApplyResult Apply(string title, IReadOnlyList<IChange> changes, IProgress<string>? p, CancellationToken ct,
        bool continueOnError)
    {
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
            // journal first (with the paths the change will write), so a crash mid-change leaves a trace
            var pending = change.Plan(session.Context) with { Id = id, Status = ChangeStatus.Pending, At = DateTimeOffset.Now };
            session.Upsert(pending);
            try
            {
                var done = change.Apply(session.Context);
                session.Upsert(done with { Id = id, Status = ChangeStatus.Applied, Error = null });
                applied++;
            }
            catch (Exception ex)
            {
                FileLog.Error($"Änderung fehlgeschlagen: {change.Description}", ex);
                var retry = change.RetryData();
                session.Upsert(pending with
                {
                    Status = ChangeStatus.Failed, At = DateTimeOffset.Now,
                    Error = $"Änderung fehlgeschlagen: {ex.Message}",
                    RetryBefore = retry?.Before, RetryAfter = retry?.After,
                });
                failed++;
                if (!continueOnError) break;
            }
        }
        return new ApplyResult(session, applied, failed, rp);
    }

    /// <summary>
    /// Applied records, plus Pending/Failed ones whose plan names what was written (e.g. files moved
    /// before a crash). Ablage records that moved nothing and uninstall entries that were never removed are left out.
    /// </summary>
    public static bool IsUndoable(ChangeRecord r) =>
        (r.Status == ChangeStatus.Applied || ((r.Status is ChangeStatus.Pending or ChangeStatus.Failed) && r.After.Count > 0))
        && !MovedNothing(r) && !RemovedNoKey(r) && !NotRestorable(r);

    /// <summary>An appx record whose package files are gone (installed from the Store): only the Store can bring it back.</summary>
    static bool NotRestorable(ChangeRecord r) => r.Kind == "appx" && r.After.GetValueOrDefault("restorable") == "false";

    /// <summary>A regkey record that stopped before its backup (e.g. the entry vanished before Apply):
    /// RegKeyChange writes the backup before it deletes anything, so no backup file means nothing was removed.</summary>
    static bool RemovedNoKey(ChangeRecord r)
    {
        if (r.Kind != "regkey" || r.Status == ChangeStatus.Applied || r.After.ContainsKey("sha256")) return false;
        var backup = r.After.GetValueOrDefault("backup");
        try { return string.IsNullOrWhiteSpace(backup) || !File.Exists(backup); }
        catch (Exception) { return false; }
    }

    /// <summary>An Ablage record (temp/file/cache/folder) whose Apply moved nothing (found nothing or a refused folder move).</summary>
    public static bool MovedNothing(ChangeRecord r) =>
        r.Kind is "temp" or "file" or "cache" or "folder" && r.After.GetValueOrDefault("moved") == "0";

    public UndoResult UndoSession(SessionJournal s, IProgress<UndoProgress>? p = null) =>
        Undo(s, s.Records.Where(IsUndoable).Reverse().ToList(), p);

    public UndoResult UndoOne(SessionJournal s, string recordId, IProgress<UndoProgress>? p = null) =>
        Undo(s, s.Records.Where(r => r.Id == recordId && IsUndoable(r)).ToList(), p);

    UndoResult Undo(SessionJournal s, IReadOnlyList<ChangeRecord> records, IProgress<UndoProgress>? p)
    {
        int undone = 0, skipped = 0, failed = 0;
        var msgs = new List<string>();

        void Skip(ChangeRecord r, string msg)
        {
            s.Upsert(r with { Status = ChangeStatus.SkippedDrift, Error = msg });
            msgs.Add(msg);
            skipped++;
        }

        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var index = i + 1;
            var line = $"({index}/{records.Count}) {r.Description}";
            p?.Report(new UndoProgress(index, records.Count, line));
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
                if (change is IProgressUndo progressive && p is not null)
                    progressive.Undo(r, s.Context, n =>
                    {
                        if (n > 0 && n % FileProgressStep == 0)
                            p.Report(new UndoProgress(index, records.Count, $"{line} – {n} Dateien zurückgeholt"));
                    });
                else
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
