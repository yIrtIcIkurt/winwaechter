using WinWaechter.Core.Security.Checks;
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

// In-memory firewall rule list; never touches the real firewall.
public sealed class FakeFirewallRules : IFirewallRules
{
    readonly Dictionary<string, FirewallRuleInfo> _rules = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Added { get; } = [];
    public List<string> Removed { get; } = [];

    public FirewallRuleInfo? Get(string name) => _rules.GetValueOrDefault(name);

    public void AddInboundBlock(string name, string program)
    {
        Added.Add(name);
        _rules[name] = new(name, program, true, true, true);
    }

    public void Remove(string name)
    {
        Removed.Add(name);
        _rules.Remove(name);
    }

    // Puts a rule there as if somebody else had made it.
    public FakeFirewallRules Seed(string name, string? program, bool inbound = true, bool block = true)
    {
        _rules[name] = new(name, program, inbound, block, true);
        return this;
    }
}
