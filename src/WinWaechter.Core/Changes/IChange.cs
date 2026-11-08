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
}

// Registered per Kind, used for undo from disk
public interface IChangeFactory { IChange Rehydrate(ChangeRecord r); }
