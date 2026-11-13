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

// In-memory firewall rule list (names may repeat, like in the real firewall); never touches the real firewall.
public sealed class FakeFirewallRules : IFirewallRules
{
    readonly List<FirewallRuleInfo> _rules = [];

    public List<string> Added { get; } = [];
    public List<string> Removed { get; } = [];

    public FirewallRuleInfo? Get(string name) => _rules.FirstOrDefault(r => Same(r.Name, name));

    public int Count(string name) => _rules.Count(r => Same(r.Name, name));

    public void AddInboundBlock(string name, string program)
    {
        Added.Add(name);
        _rules.Add(new(name, program, true, true, true));
    }

    public void Remove(string name, string program)
    {
        Removed.Add(name);
        _rules.RemoveAll(r => Same(r.Name, name) && Same(r.Program, program));
    }

    // Puts a rule there as if somebody else had made it; replaces a rule of that name unless add is true.
    public FakeFirewallRules Seed(string name, string? program, bool inbound = true, bool block = true, bool add = false)
    {
        if (!add) _rules.RemoveAll(r => Same(r.Name, name));
        _rules.Add(new(name, program, inbound, block, true));
        return this;
    }

    // Test helper: drops every rule of that name.
    public void Clear(string name) => _rules.RemoveAll(r => Same(r.Name, name));

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
