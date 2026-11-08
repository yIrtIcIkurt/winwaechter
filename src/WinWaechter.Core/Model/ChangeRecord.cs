namespace WinWaechter.Core.Model;

public enum ChangeStatus { Pending, Applied, Failed, Undone, SkippedDrift }

public enum DriftState { Unchanged, Changed, Gone }

// Persisted description of one change; Before/After hold everything needed to undo it.
public sealed record ChangeRecord(string Id, string Kind, string Target, string Description,
    Dictionary<string, string?> Before, Dictionary<string, string?> After,
    ChangeStatus Status, DateTimeOffset At, string? Error = null);
