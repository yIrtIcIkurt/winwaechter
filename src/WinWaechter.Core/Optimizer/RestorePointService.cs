using System.Management;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Optimizer;

/// <summary>RecentExists: no new point because Windows keeps one per 24 hours and one is younger (RecentAt).</summary>
public sealed record RestorePointResult(bool Created, string Message, bool RecentExists = false, DateTimeOffset? RecentAt = null)
{
    /// <summary>Windows refused because the Computerschutz of the system drive is off (the Sicherheit page switches it on).</summary>
    public bool ProtectionOff { get; init; }
}

// Creates a Windows restore point via WMI. Never throws.
public sealed class RestorePointService
{
    internal const string ScopePath = @"\\.\root\default";

    const string NotCreated =
        "Kein neuer Wiederherstellungspunkt (Computerschutz aus oder Windows-Sperre von 24 Stunden). Die eigene Sicherung ist trotzdem angelegt.";

    public RestorePointResult TryCreate(string description)
    {
        try
        {
            var scope = new ManagementScope(ScopePath);
            scope.Connect();
            var before = Sequences(scope);

            using (var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
            using (var args = cls.GetMethodParameters("CreateRestorePoint"))
            {
                args["Description"] = description;
                args["RestorePointType"] = 12;   // MODIFY_SETTINGS
                args["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
                using var _ = cls.InvokeMethod("CreateRestorePoint", args, null);
            }

            var after = Sequences(scope);
            if (after.Any(n => !before.Contains(n))) return new RestorePointResult(true, "Wiederherstellungspunkt erstellt.");
            return Newest(scope) is { } at && DateTimeOffset.Now - at < TimeSpan.FromHours(24)
                ? new RestorePointResult(false, $"Ein Wiederherstellungspunkt von {at:HH:mm} besteht schon.", true, at)
                : new RestorePointResult(false, NotCreated);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Wiederherstellungspunkt fehlgeschlagen", ex);
            return new RestorePointResult(false, MessageFor(ex)) { ProtectionOff = ex.HResult == ServiceDisabled };
        }
    }

    const int ServiceDisabled = unchecked((int)0x80070422);   // ERROR_SERVICE_DISABLED: protection of the system drive is off

    /// <summary>Plain text for a refused restore point; Windows answers 0x80070422 when the system drive is not protected.</summary>
    public static string MessageFor(Exception ex) => ex.HResult == ServiceDisabled
        ? "Der Computerschutz ist für das Systemlaufwerk ausgeschaltet. Auf der Seite „Sicherheit“ lässt er sich mit einem Klick einschalten."
        : NotCreated;

    static HashSet<uint> Sequences(ManagementScope scope)
    {
        var set = new HashSet<uint>();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
        foreach (ManagementBaseObject o in s.Get())
            using (o) set.Add(Convert.ToUInt32(o["SequenceNumber"]));
        return set;
    }

    // CreationTime is a CIM datetime ("yyyyMMddHHmmss.ffffff+ooo"); null when none or unreadable.
    static DateTimeOffset? Newest(ManagementScope scope)
    {
        DateTimeOffset? newest = null;
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CreationTime FROM SystemRestore"));
        foreach (ManagementBaseObject o in s.Get())
        {
            using (o)
            {
                try
                {
                    var at = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(o["CreationTime"]?.ToString()));
                    if (newest is null || at > newest) newest = at;
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException) { }
            }
        }
        return newest;
    }
}
