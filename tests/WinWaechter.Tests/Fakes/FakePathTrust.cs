using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Everything is admin-only except the paths listed in Untrusted; records every query.
public sealed class FakePathTrust : IPathTrust
{
    public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    public bool IsAdminOnly(string path)
    {
        lock (Calls) Calls.Add(path);
        return !Untrusted.Contains(Path.TrimEndingDirectorySeparator(path));
    }
}
