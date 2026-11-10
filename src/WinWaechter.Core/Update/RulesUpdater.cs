using WinWaechter.Core.Logging;
using WinWaechter.Core.Rules;

namespace WinWaechter.Core.Update;

// Acceptance is null when nothing could be fetched. Message is German and safe to show.
public sealed record RulesUpdateResult(RulesAcceptance? Acceptance, string Message);

// Fetch + accept into the store. New rules take effect on the next start (no swap at runtime).
public sealed class RulesUpdater
{
    readonly RulesFetcher _fetcher;
    readonly RulesStore _store;
    readonly SemVer _app;
    int _current;

    public RulesUpdater(RulesFetcher fetcher, RulesStore store, int currentVersion, SemVer app)
    {
        _fetcher = fetcher;
        _store = store;
        _current = currentVersion;
        _app = app;
    }

    // Never throws except OperationCanceledException (when ct is cancelled).
    public async Task<RulesUpdateResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var (json, sig) = await _fetcher.FetchAsync(ct).ConfigureAwait(false);
            var result = _store.TryAccept(json, sig, _current, _app);
            switch (result)
            {
                case RulesAcceptance.Accepted:
                    var v = RuleSet.Parse(json).RulesVersion;
                    _current = v;
                    return new(result, $"Neue Regeln (Version {v}) geladen. Sie gelten ab dem nächsten Start.");
                case RulesAcceptance.NotNewer:
                    return new(result, "Die Regeln sind aktuell.");
                case RulesAcceptance.BadSignature:
                    FileLog.Warn("Regel-Update: Signatur ungültig, verworfen.");
                    return new(result, "Die geladenen Regeln sind nicht gültig signiert und wurden verworfen.");
                case RulesAcceptance.AppTooOld:
                    return new(result, $"Die neuen Regeln brauchen WinWächter {RuleSet.Parse(json).MinAppVersion} oder neuer.");
                default:
                    FileLog.Warn("Regel-Update: Datei fehlerhaft, verworfen.");
                    return new(result, "Die geladenen Regeln sind fehlerhaft und wurden verworfen.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            FileLog.Warn("Regel-Update fehlgeschlagen", ex);
            return new(null, $"Regeln konnten nicht geladen werden: {ex.Message}");
        }
    }
}
