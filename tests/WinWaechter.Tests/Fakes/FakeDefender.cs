using WinWaechter.Core.Security.Defender;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeDefender : IDefender
{
    public int PuaMode { get; set; }
    public List<int> SetPuaCalls { get; } = [];

    public Task<DefenderStatus> GetStatusAsync() =>
        Task.FromResult(new DefenderStatus(true, true, true, PuaMode, null, 0, null, null, null));

    public Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync() =>
        Task.FromResult<IReadOnlyList<DefenderThreat>>([]);

    public Task<int> UpdateSignaturesAsync(CancellationToken ct) => Task.FromResult(0);
    public Task<int> ScanAsync(ScanKind kind, CancellationToken ct) => Task.FromResult(0);
    public Task<int> ScanPathAsync(string path, CancellationToken ct) => Task.FromResult(0);
    public Task<int> RemoveActiveThreatsAsync() => Task.FromResult(0);

    public Task SetPuaModeAsync(int mode)
    {
        SetPuaCalls.Add(mode);
        PuaMode = mode;
        return Task.CompletedTask;
    }
}
