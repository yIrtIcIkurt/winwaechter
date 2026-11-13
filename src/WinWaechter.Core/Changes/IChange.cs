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

    /// <summary>
    /// The identity an Applied record would carry (Before/After), so this kind's own FromRecord can rebuild
    /// the change for a retry after a failure. Holds no old-state values. Null: this kind cannot be retried.
    /// </summary>
    (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() => null;
}

/// <summary>A change whose undo is limited; the label is shown in the confirmation and in the Verlauf.</summary>
public interface IUndoLimit
{
    string? UndoLimit { get; }
}

public static class UndoLimits
{
    public const string Partial = "Nur teilweise rückgängig";
    public const string StoreOnly = "Nur über den Store wiederherstellbar";
}

public static class ChangeText
{
    /// <summary>The line in a confirmation: the description plus the undo limit in parentheses, if any.</summary>
    public static string ConfirmLine(IChange c) =>
        c is IUndoLimit { UndoLimit: { } limit } ? $"{c.Description} ({limit})" : c.Description;
}

// Registered per Kind, used for undo from disk
public interface IChangeFactory { IChange Rehydrate(ChangeRecord r); }
