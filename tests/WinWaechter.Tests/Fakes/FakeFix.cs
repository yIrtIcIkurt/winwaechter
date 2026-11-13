using WinWaechter.Core.Performance;
using WinWaechter.Core.Actions;
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

public sealed class FakeUpdateInstaller : IWindowsUpdateInstaller
{
    public UpdateInstallResult Result { get; set; } = new(0, 0, 0, false);
    public int Calls { get; private set; }

    public Task<UpdateInstallResult> InstallAsync(IProgress<string>? p, CancellationToken ct)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }
}

public sealed class FakeRestarter : IRestarter
{
    public int Restarts { get; private set; }
    public void Restart() => Restarts++;
}

// In-memory Computerschutz; Enable is recorded and switches it on. There is deliberately no way to switch it off.
public sealed class FakeRestoreConfig : ISystemRestoreConfig
{
    public bool? State { get; set; }
    public Exception? EnableError { get; set; }
    public Exception? ReadError { get; set; }
    public List<string> EnableCalls { get; } = [];

    public bool? IsEnabled(string drive) => ReadError is { } e ? throw e : State;

    public void Enable(string drive)
    {
        EnableCalls.Add(drive);
        if (EnableError is { } e) throw e;
        State = true;
    }
}

// In-memory power plans; never touches powrprof.
public sealed class FakePowerPlans : IPowerPlans
{
    public static readonly Guid BalancedId = PowerModeIds.BalancedPlan;
    public static readonly Guid HighId = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid SaverId = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    public List<PowerPlan> Plans { get; } =
        [new(BalancedId, "Ausgewogen"), new(HighId, "Höchstleistung"), new(SaverId, "Energiesparmodus")];
    public Guid? ActiveId { get; set; } = BalancedId;
    public List<Guid> SetCalls { get; } = [];

    public IReadOnlyList<PowerPlan> List() => Plans.ToList();
    public Guid? Active() => ActiveId;

    public void SetActive(Guid id)
    {
        SetCalls.Add(id);
        ActiveId = id;
    }
}
