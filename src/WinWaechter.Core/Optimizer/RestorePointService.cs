using System.Management;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Optimizer;

public sealed record RestorePointResult(bool Created, string Message);

// Creates a Windows restore point via WMI. Never throws.
public sealed class RestorePointService
{
    const string NotCreated =
        "Kein neuer Wiederherstellungspunkt (Computerschutz aus oder Windows-Sperre von 24 Stunden). Die eigene Sicherung ist trotzdem angelegt.";

    public RestorePointResult TryCreate(string description)
    {
        try
        {
            var scope = new ManagementScope(@"\.\root\default");
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
            return after.Any(n => !before.Contains(n))
                ? new RestorePointResult(true, "Wiederherstellungspunkt erstellt.")
                : new RestorePointResult(false, NotCreated);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Wiederherstellungspunkt fehlgeschlagen", ex);
            return new RestorePointResult(false, NotCreated);
        }
    }

    static HashSet<uint> Sequences(ManagementScope scope)
    {
        var set = new HashSet<uint>();
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
        foreach (ManagementBaseObject o in s.Get())
            using (o) set.Add(Convert.ToUInt32(o["SequenceNumber"]));
        return set;
    }
}
