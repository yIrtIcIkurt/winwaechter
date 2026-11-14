using WinWaechter.Core.Programs;

namespace WinWaechter.Tests.Fakes;

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
