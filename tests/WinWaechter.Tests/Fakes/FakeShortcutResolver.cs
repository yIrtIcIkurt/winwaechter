using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Target per .lnk path; anything not set resolves to null.
public sealed class FakeShortcutResolver : IShortcutResolver
{
    readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);

    public FakeShortcutResolver Set(string lnkPath, string? target) { _targets[lnkPath] = target; return this; }

    public string? ResolveTarget(string lnkPath) => _targets.GetValueOrDefault(lnkPath);
}
