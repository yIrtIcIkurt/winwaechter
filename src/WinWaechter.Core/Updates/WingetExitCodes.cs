namespace WinWaechter.Core.Updates;

public enum UpdateOutcome { Updated, AlreadyCurrent, RestartNeeded, Failed, Skipped }

public sealed record UpdateResult(WingetPackage Package, UpdateOutcome Outcome, int ExitCode, string Text);

/// <summary>
/// Plain German texts for winget's results. Codes and meanings from winget-cli's returnCodes.md
/// (checked 2026-11-14); anything else gets its code and a pointer to the log.
/// </summary>
public static class WingetExitCodes
{
    public static (UpdateOutcome Outcome, string Text) Map(int code) => unchecked((uint)code) switch
    {
        0 => (UpdateOutcome.Updated, "Aktualisiert."),
        0x8A15002B => (UpdateOutcome.AlreadyCurrent, "War bereits aktuell."),                 // UPDATE_NOT_APPLICABLE
        0x8A150109 or 0x8A15010B => (UpdateOutcome.RestartNeeded,                             // REBOOT_REQUIRED_TO_FINISH, REBOOT_INITIATED
            "Neustart nötig, damit das Update fertig wird."),
        0x8A15010A => (UpdateOutcome.Failed, "Erst nach einem Neustart möglich. Starten Sie den Rechner neu und versuchen Sie es erneut."),
        0x8A150101 or 0x8A150103 or 0x8A150111 => (UpdateOutcome.Failed,                     // PACKAGE_IN_USE, FILE_IN_USE, IN_USE_BY_APPLICATION
            "Das Programm läuft gerade. Schließen Sie es und versuchen Sie es erneut."),
        0x8A150102 => (UpdateOutcome.Failed, "Eine andere Installation läuft gerade. Versuchen Sie es später erneut."),
        0x8A150008 => (UpdateOutcome.Failed, "Download fehlgeschlagen. Prüfen Sie die Internetverbindung."),
        0x8A150011 => (UpdateOutcome.Failed,
            "Die heruntergeladene Datei stimmt nicht. Das Update wurde aus Sicherheitsgründen nicht installiert."),
        0x8A150010 => (UpdateOutcome.Failed, "Für diesen Rechner gibt es kein passendes Installationsprogramm."),
        0x8A150105 => (UpdateOutcome.Failed, "Auf dem Laufwerk ist kein Platz mehr frei."),
        0x8A15010C => (UpdateOutcome.Failed, "Die Installation wurde abgebrochen."),
        0x8A15010F => (UpdateOutcome.Failed, "Eine Richtlinie verhindert die Installation."),
        0x8A150114 => (UpdateOutcome.Failed, "Dieses Programm lässt sich nicht über winget aktualisieren."),
        0x8A15008E => (UpdateOutcome.Failed,                                                  // UPDATE_INSTALL_TECHNOLOGY_MISMATCH
            "Das Update nutzt eine andere Installationsart als die installierte Fassung. Aktualisieren Sie es im Programm selbst."),
        0x80073D28 => (UpdateOutcome.Failed,                                                  // AppX: package service needs admin
            "Diese Fassung bringt einen Hintergrunddienst mit und braucht Administratorrechte. Aktualisieren Sie es im Programm selbst oder im Microsoft Store."),
        _ => (UpdateOutcome.Failed, $"Fehlgeschlagen (Code 0x{code:X8}), Details im Protokoll."),
    };
}
