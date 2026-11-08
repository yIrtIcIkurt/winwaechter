using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Changes;

public interface IChange
{
    string Kind { get; }              // "autostart", "service", "task", "file", "pua"
    string Target { get; }
    string Description { get; }       // German, shown in preview + history
    ChangeRecord Apply(SessionContext ctx);          // backs up first, returns Applied record
    void Undo(ChangeRecord record, SessionContext ctx);
    DriftState CheckDrift(ChangeRecord record);      // compare live state with record.After

    /// <summary>
    /// The Pending record journalled before Apply runs. Changes that write files fill in where
    /// (Before/After), so a failure or crash midway can still be undone.
    /// </summary>
    ChangeRecord Plan(SessionContext ctx) =>
        new(Guid.NewGuid().ToString("N"), Kind, Target, Description, new(), new(), ChangeStatus.Pending, DateTimeOffset.Now);
}

// Registered per Kind, used for undo from disk
public interface IChangeFactory { IChange Rehydrate(ChangeRecord r); }
