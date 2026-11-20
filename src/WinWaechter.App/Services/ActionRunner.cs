using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.Services;

/// <summary>
/// Shared confirm/log/error layer for one-off actions. Must not be called from inside the caller's own
/// gate.RunAsync (the gate is not reentrant). Defender actions (<see cref="SystemAction.UsesDefender"/>)
/// also wait for <see cref="DefenderActivity"/>: never two Defender jobs at once. Long actions
/// (<see cref="SystemAction.SkipGate"/>) run outside the change gate, guarded by their own <see cref="ActivityLock"/>.
/// </summary>
public sealed class ActionRunner(IDialogService dialogs, ChangeGate gate, DefenderActivity? defender = null,
    IReadOnlyDictionary<string, ActivityLock>? locks = null, ActionStampStore? stamps = null)
{
    public const string FinalNote = "Diese Aktion ist endgültig und lässt sich nicht im Verlauf rückgängig machen.";

    /// <summary>The lock for a key (null when unknown).</summary>
    public ActivityLock? Lock(string? key) => key is not null && locks is not null && locks.TryGetValue(key, out var l) ? l : null;

    /// <summary>Null when the user declined; otherwise the outcome (busy gate, lock or Defender and errors included).</summary>
    public async Task<ActionOutcome?> RunAsync(object owner, SystemAction a, IProgress<string>? progress, CancellationToken ct = default)
    {
        // a record `with` expression can bypass the constructor guard
        if (a.Final && a.ConfirmText is null)
            throw new ArgumentException("Eine endgültige Aktion braucht einen Bestätigungstext.", nameof(a));
        var activity = Lock(a.LockKey);
        if (a.SkipGate && activity is null)
            throw new ArgumentException("Eine lange Aktion ohne Änderungssperre braucht eine eigene Sperre.", nameof(a));
        var guardDefender = a.UsesDefender && defender is not null;
        if (guardDefender && defender!.IsBusy) return new ActionOutcome(false, DefenderActivity.BusyText);
        if (activity is { IsRunning: true }) return new ActionOutcome(false, activity.BusyText);
        if (a.ConfirmText is not null)
        {
            var message = a.Final ? $"{a.ConfirmText} {FinalNote}" : a.ConfirmText;
            if (!dialogs.Confirm(a.Title, message, a.ButtonText, a.Items, a.Final)) return null;
        }
        // checked again: something may have started while the confirmation was open
        if (guardDefender && !defender!.TryBegin()) return new ActionOutcome(false, DefenderActivity.BusyText);
        if (activity is not null && !activity.TryBegin())
        {
            if (guardDefender) defender!.End();
            return new ActionOutcome(false, activity.BusyText);
        }

        try
        {
            ActionOutcome outcome = new(false, ChangeGate.BusyHint);
            async Task Run()
            {
                FileLog.Info($"Aktion: {a.Title}");
                try
                {
                    outcome = await a.RunAsync(progress, ct);
                    FileLog.Info($"Aktion {a.Title}: {outcome.Message}");
                }
                catch (OperationCanceledException)
                {
                    FileLog.Info($"Aktion abgebrochen: {a.Title}");
                    outcome = new ActionOutcome(false, "Abgebrochen.");
                }
                catch (Exception ex)
                {
                    FileLog.Error($"Aktion {a.Title} fehlgeschlagen", ex);
                    dialogs.Inform($"{a.Title} fehlgeschlagen", ex.Message);
                    outcome = new ActionOutcome(false, ex.Message);
                }
            }

            bool ran;
            if (a.SkipGate)
            {
                await Run();
                ran = true;
            }
            else ran = await gate.RunAsync(owner, Run);
            if (!ran) return new ActionOutcome(false, ChangeGate.BusyHint);
            if (a.Stamp) stamps?.Set(new ActionStamp(a.Id, DateTimeOffset.Now, outcome.Success, outcome.Message, outcome.Link));
            return outcome;
        }
        finally
        {
            activity?.End();
            if (guardDefender) defender!.End();
        }
    }
}
