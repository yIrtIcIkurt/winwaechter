using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Policy values WinWächter "set itself"; everything else counts as foreign.
public sealed class FakePolicyOwnership : IPolicyOwnership
{
    readonly HashSet<(Hive, string, string)> _ours = [];

    public FakePolicyOwnership Own(Hive h, string key, string name)
    {
        _ours.Add(Id(h, key, name));
        return this;
    }

    public bool IsOurs(Hive h, string key, string name) => _ours.Contains(Id(h, key, name));

    static (Hive, string, string) Id(Hive h, string key, string name) =>
        (h, key.Trim('\\').ToUpperInvariant(), name.ToUpperInvariant());
}
