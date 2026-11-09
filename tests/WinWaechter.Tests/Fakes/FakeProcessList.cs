using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeProcessList(params RunningProcess[] processes) : IProcessList
{
    public List<RunningProcess> List { get; } = [.. processes];

    /// <summary>When set, Snapshot throws it.</summary>
    public Exception? Throw { get; set; }

    public IReadOnlyList<RunningProcess> Snapshot() => Throw is { } e ? throw e : List.ToList();
}
