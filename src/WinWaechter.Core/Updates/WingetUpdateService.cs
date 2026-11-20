using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Optimizer;

namespace WinWaechter.Core.Updates;

/// <summary>Reads the offered updates and installs a selection one by one (an action: no undo).</summary>
public sealed class WingetUpdateService(IWingetClient client, UpdateFilter filter, Func<string, RestorePointResult> restorePoint)
{
    public const string ActionId = "winget-upgrade";

    /// <summary>The restore point creator the caller wraps (RestorePointGate) for <see cref="InstallAction"/>.</summary>
    public Func<string, RestorePointResult> RestorePoint => restorePoint;

    public async Task<(WingetList List, IReadOnlyList<WingetPackage> Offered, IReadOnlyList<WingetPackage> Hidden)> LoadAsync(
        bool acceptSourceAgreements, CancellationToken ct)
    {
        var list = await client.ListUpgradesAsync(acceptSourceAgreements, ct).ConfigureAwait(false);
        if (list.Status != WingetListStatus.Ok) return (list, [], []);
        var (offered, hidden) = await Task.Run(() => filter.Split(list.Packages), ct).ConfigureAwait(false);
        return (list, offered, hidden);
    }

    /// <summary>
    /// Final action that updates the selection in order. ensureRestorePoint runs after the confirmation;
    /// false = nothing runs. Cancelling stops between packages, never inside a running setup.
    /// </summary>
    public SystemAction InstallAction(IReadOnlyList<WingetPackage> selected, Func<Task<bool>> ensureRestorePoint,
        Func<IReadOnlyList<UpdateResult>, Task> onDone, IProgress<ItemProgress>? items = null)
    {
        // one entry per id; the user-scope entry wins (it must run in the user's context)
        var packages = selected.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(p => p.UserScope) ?? g.First()).ToList();
        var confirm = $"{packages.Count} Programme werden aktualisiert. Ein Update lässt sich nicht über den Verlauf zurücknehmen. "
                      + "Schließen Sie die betroffenen Programme vorher; manche Installationen starten sie neu oder verlangen einen Neustart. "
                      + "Mit dem Klick nehmen Sie die Lizenzbedingungen der Updates an.";
        return new SystemAction(ActionId, "Programme aktualisieren", "Aktualisieren", confirm, true,
            (progress, ct) => RunAsync(packages, ensureRestorePoint, onDone, progress, items, ct),
            packages.Select(p => $"{p.Name}: {p.Installed} → {p.Available}").ToList())
        {
            SkipGate = true,
            LockKey = "updates",
            Stamp = true,
        };
    }

    async Task<ActionOutcome> RunAsync(IReadOnlyList<WingetPackage> packages, Func<Task<bool>> ensureRestorePoint,
        Func<IReadOnlyList<UpdateResult>, Task> onDone, IProgress<string>? progress, IProgress<ItemProgress>? items, CancellationToken ct)
    {
        if (!await ensureRestorePoint()) return new ActionOutcome(false, "Abgebrochen.");
        var results = new List<UpdateResult>();
        var cancelled = false;
        void Add(UpdateResult r)
        {
            results.Add(r);
            var state = r.Outcome switch
            {
                UpdateOutcome.Failed => ItemState.Failed,
                UpdateOutcome.Skipped => ItemState.Skipped,
                _ => ItemState.Done,
            };
            items?.Report(new ItemProgress(r.Package, state, r.Text));
        }
        for (var i = 0; i < packages.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var p = packages[i];
            progress?.Report($"Paket {i + 1} von {packages.Count}: {p.Name} …");
            items?.Report(new ItemProgress(p, ItemState.Running));
            // the protection check again, right before the start
            if (filter.Split([p]).Hidden.Count > 0)
            {
                Add(new UpdateResult(p, UpdateOutcome.Skipped, 0, "Geschützt, ausgelassen."));
                continue;
            }
            int code;
            try
            {
                code = await client.UpgradeAsync(p, line => FileLog.Info($"winget {p.Id}: {line}"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FileLog.Error($"winget {p.Id} konnte nicht gestartet werden", ex);
                Add(new UpdateResult(p, UpdateOutcome.Failed, -1, "Konnte nicht gestartet werden: " + ex.Message));
                continue;
            }
            var (outcome, text) = WingetExitCodes.Map(code);
            FileLog.Info($"winget {p.Id} {p.Installed} -> {p.Available}: 0x{code:X8}");
            Add(new UpdateResult(p, outcome, code, text));
        }
        await onDone(results).ConfigureAwait(false);

        var ok = results.Count(r => r.Outcome is UpdateOutcome.Updated or UpdateOutcome.AlreadyCurrent or UpdateOutcome.RestartNeeded);
        var failed = results.Count(r => r.Outcome == UpdateOutcome.Failed);
        var skipped = results.Count(r => r.Outcome == UpdateOutcome.Skipped);
        var message = $"{ok} aktualisiert, {failed} fehlgeschlagen";
        if (skipped > 0) message += $", {skipped} ausgelassen";
        if (results.Any(r => r.Outcome == UpdateOutcome.RestartNeeded)) message += ", Neustart nötig";
        if (cancelled) message += ", Rest abgebrochen";
        return new ActionOutcome(failed == 0 && !cancelled, message);
    }
}
