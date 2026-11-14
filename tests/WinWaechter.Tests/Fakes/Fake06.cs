using WinWaechter.Core.Programs;
using WinWaechter.Core.Updates;

namespace WinWaechter.Tests.Fakes;

// List result is configurable; UpgradeHandler decides the exit code per package (default 0).
public sealed class FakeWinget : IWingetClient
{
    public WingetList List { get; set; } = new(WingetListStatus.Ok, [], null);
    public List<bool> ListCalls { get; } = [];
    public List<WingetPackage> Upgraded { get; } = [];
    public Func<WingetPackage, int>? UpgradeHandler { get; set; }

    public Task<WingetList> ListUpgradesAsync(bool acceptSourceAgreements, CancellationToken ct)
    {
        ListCalls.Add(acceptSourceAgreements);
        return Task.FromResult(List);
    }

    public Task<int> UpgradeAsync(WingetPackage p, Action<string>? onLine, CancellationToken ct)
    {
        Upgraded.Add(p);
        return Task.FromResult(UpgradeHandler?.Invoke(p) ?? 0);
    }
}

// Records user-context runs; Handler decides the exit code.
public sealed class FakeUserProcessRunner : IUserProcessRunner
{
    public List<(string Exe, string Args)> Calls { get; } = [];
    public Func<string, string, int>? Handler { get; set; }

    public Task<int> RunAsUserAsync(string exe, string args, CancellationToken ct)
    {
        lock (Calls) Calls.Add((exe, args));
        return Task.FromResult(Handler?.Invoke(exe, args) ?? 0);
    }
}
