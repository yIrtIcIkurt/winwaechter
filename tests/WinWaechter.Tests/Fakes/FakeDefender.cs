using WinWaechter.Core.Security.Defender;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeDefender : IDefender
{
    public int PuaMode { get; set; }
    public List<int> SetPuaCalls { get; } = [];

    /// <summary>When set, GetStatusAsync fails with it.</summary>
    public Exception? StatusError { get; set; }

    /// <summary>GetStatusAsync waits this long before answering.</summary>
    public TimeSpan StatusDelay { get; set; }

    public async Task<DefenderStatus> GetStatusAsync()
    {
        if (StatusDelay > TimeSpan.Zero) await Task.Delay(StatusDelay);
        if (StatusError is { } e) throw e;
        return new DefenderStatus(true, true, true, PuaMode, null, 0, null, null, null);
    }

    /// <summary>Order of operations, e.g. "update", "scan:Quick", "remove".</summary>
    public List<string> Calls { get; } = [];
    public List<DefenderThreat> Threats { get; } = [];
    public Exception? UpdateError { get; set; }

    public Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync() =>
        Task.FromResult<IReadOnlyList<DefenderThreat>>(Threats.ToList());

    public Task<int> UpdateSignaturesAsync(CancellationToken ct)
    {
        Calls.Add("update");
        return UpdateError is { } e ? Task.FromException<int>(e) : Task.FromResult(0);
    }

    public Task<int> ScanAsync(ScanKind kind, CancellationToken ct)
    {
        Calls.Add("scan:" + kind);
        return Task.FromResult(0);
    }

    public Task<int> ScanPathAsync(string path, CancellationToken ct) => Task.FromResult(0);

    public Task<int> RemoveActiveThreatsAsync()
    {
        Calls.Add("remove");
        var n = Threats.Count(t => t.Active);
        Threats.RemoveAll(t => t.Active);
        return Task.FromResult(n);
    }

    public Task SetPuaModeAsync(int mode)
    {
        SetPuaCalls.Add(mode);
        PuaMode = mode;
        return Task.CompletedTask;
    }
}
