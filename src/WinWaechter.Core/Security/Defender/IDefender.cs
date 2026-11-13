namespace WinWaechter.Core.Security.Defender;

public sealed record DefenderStatus(bool Available, bool RealTime, bool TamperProtected, int PuaMode /*0 off,1 on,2 audit*/,
    DateTimeOffset? SignatureUpdated, int SignatureAgeDays, DateTimeOffset? LastQuickScan, DateTimeOffset? LastFullScan, string? Note);

public sealed record DefenderThreat(long ThreatId, string Name, int Severity, IReadOnlyList<string> Resources, bool Active, string StatusText);

public enum ScanKind { Quick = 1, Full = 2 }

// Microsoft Defender is the only scan engine; this is just its client.
public interface IDefender
{
    Task<DefenderStatus> GetStatusAsync();
    Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync();
    Task<int> UpdateSignaturesAsync(CancellationToken ct);
    Task<int> ScanAsync(ScanKind kind, CancellationToken ct);   // ct → "MpCmdRun -Scan -Cancel"
    Task<int> ScanPathAsync(string path, CancellationToken ct);
    Task<int> RemoveActiveThreatsAsync();
    Task SetPuaModeAsync(int mode);
    Task EnableRealTimeAsync();   // switching on only; WinWächter never switches protection off
}
