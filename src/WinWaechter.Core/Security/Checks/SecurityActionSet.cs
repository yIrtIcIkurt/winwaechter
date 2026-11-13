using WinWaechter.Core.Actions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>The one-off actions of the security rows. Cancelling propagates; failures come back as outcomes.</summary>
public sealed record SecurityActionSet(SystemAction EnableRealTime, SystemAction UpdateSignatures, SystemAction InstallUpdates, SystemAction Restart)
{
    public const string RealTimeBlocked = "Windows hat das Einschalten blockiert, vermutlich durch den Manipulationsschutz. Bitte schalten Sie den Echtzeitschutz in der Windows-Sicherheit ein.";

    public static SecurityActionSet Create(IDefender d, IWindowsUpdateInstaller u, IRestarter r) => new(
        new SystemAction("enable-realtime", "Echtzeitschutz einschalten", "Einschalten",
            "WinWächter schaltet den Echtzeitschutz von Defender ein. WinWächter schaltet Virenschutz nie wieder ab.", true,
            async (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                try { await d.EnableRealTimeAsync().ConfigureAwait(false); }
                catch (InvalidOperationException ex)
                {
                    // Tamper protection often makes Set-MpPreference fail; the status below decides.
                    FileLog.Warn("Echtzeitschutz einschalten fehlgeschlagen", ex);
                }
                var status = await d.GetStatusAsync().ConfigureAwait(false);
                return status.RealTime
                    ? new ActionOutcome(true, "Der Echtzeitschutz ist eingeschaltet.")
                    : new ActionOutcome(false, RealTimeBlocked, SystemLinks.DefenderThreat);
            }),
        new SystemAction("update-signatures", "Virendefinitionen aktualisieren", "Jetzt aktualisieren", null, false,
            async (p, ct) =>
            {
                p?.Report("Virendefinitionen werden aktualisiert …");
                var exit = await d.UpdateSignaturesAsync(ct).ConfigureAwait(false);
                return exit == 0
                    ? new ActionOutcome(true, "Die Virendefinitionen sind aktuell.")
                    : new ActionOutcome(false, $"Die Virendefinitionen ließen sich nicht aktualisieren (Code {exit}).");
            }),
        new SystemAction("install-updates", "Windows-Updates installieren", "Updates installieren",
            "Updates lassen sich nicht über WinWächter zurücknehmen; einzelne können Sie in Windows unter Updateverlauf deinstallieren.", true,
            async (p, ct) =>
            {
                var res = await u.InstallAsync(p, ct).ConfigureAwait(false);
                var parts = new List<string> { $"{res.Installed} installiert" };
                if (res.Failed > 0) parts.Add($"{res.Failed} fehlgeschlagen");
                if (res.NeedsWindowsUpdate > 0) parts.Add($"{res.NeedsWindowsUpdate} nur in Windows Update installierbar");
                var msg = string.Join(", ", parts) + ".";
                if (res.RebootRequired) msg += " Ein Neustart ist nötig.";
                return new ActionOutcome(res.Failed == 0, msg);
            }),
        new SystemAction("restart", "Rechner neu starten", "Jetzt neu starten",
            "Speichern Sie offene Arbeit. Der Rechner startet sofort neu.", true,
            async (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(r.Restart, ct).ConfigureAwait(false);
                return new ActionOutcome(true, "Der Rechner startet neu.");
            }));
}
