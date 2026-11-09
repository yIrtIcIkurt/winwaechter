using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeProcessList(params RunningProcess[] processes) : IProcessList
{
    public List<RunningProcess> List { get; } = [.. processes];

    public IReadOnlyList<RunningProcess> Snapshot() => List.ToList();
}
