using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.Services;

/// <summary>
/// Shared confirm/log/error layer for one-off actions. Must not be called from inside the caller's own
/// gate.RunAsync (the gate is not reentrant).
/// </summary>
public sealed class ActionRunner(IDialogService dialogs, ChangeGate gate)
{
    public const string FinalNote = "Diese Aktion ist endgültig und lässt sich nicht im Verlauf rückgängig machen.";

    /// <summary>Null when the user declined; otherwise the outcome (busy gate and errors included).</summary>
    public async Task<ActionOutcome?> RunAsync(object owner, SystemAction a, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (a.ConfirmText is not null)
        {
            var message = a.Final ? $"{a.ConfirmText} {FinalNote}" : a.ConfirmText;
            if (!dialogs.Confirm(a.Title, message, a.ButtonText, a.Items, a.Final)) return null;
        }

        ActionOutcome outcome = new(false, ChangeGate.BusyHint);
        var ran = await gate.RunAsync(owner, async () =>
        {
            FileLog.Info($"Aktion: {a.Title}");
            try
            {
                outcome = await a.RunAsync(progress, ct);
                FileLog.Info($"Aktion {a.Title}: {outcome.Message}");
            }
            catch (Exception ex)
            {
                FileLog.Error($"Aktion {a.Title} fehlgeschlagen", ex);
                dialogs.Inform($"{a.Title} fehlgeschlagen", ex.Message);
                outcome = new ActionOutcome(false, ex.Message);
            }
        });
        return ran ? outcome : new ActionOutcome(false, ChangeGate.BusyHint);
    }
}
