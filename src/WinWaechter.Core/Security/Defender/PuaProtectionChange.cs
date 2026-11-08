using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Security.Defender;

/// <summary>Turns on Defender's protection against potentially unwanted apps.</summary>
public sealed class PuaProtectionChange(IDefender defender) : IChange
{
    public string Kind => "pua";
    public string Target => "Defender PUA-Schutz";
    public string Description => "PUA-Schutz von Defender einschalten";

    public static PuaProtectionChange FromRecord(ChangeRecord r, IDefender d) => new(d);

    public ChangeRecord Apply(SessionContext ctx)
    {
        var status = defender.GetStatusAsync().GetAwaiter().GetResult();
        if (!status.Available)
            throw new InvalidOperationException("Defender ist nicht aktiv – PUA-Schutz kann nicht geändert werden.");
        var before = status.PuaMode;
        defender.SetPuaModeAsync(1).GetAwaiter().GetResult();
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["mode"] = before.ToString() }, new() { ["mode"] = "1" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx) =>
        defender.SetPuaModeAsync(int.Parse(record.Before["mode"]!)).GetAwaiter().GetResult();

    public DriftState CheckDrift(ChangeRecord record)
    {
        var live = defender.GetStatusAsync().GetAwaiter().GetResult().PuaMode;
        return live == int.Parse(record.After.GetValueOrDefault("mode") ?? "1") ? DriftState.Unchanged : DriftState.Changed;
    }
}

public static class DefenderRegistration
{
    public static void Register(ChangeFactory f, IDefender d) =>
        f.Register("pua", r => PuaProtectionChange.FromRecord(r, d));
}
