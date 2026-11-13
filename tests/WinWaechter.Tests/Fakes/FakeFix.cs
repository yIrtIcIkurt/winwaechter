using WinWaechter.Core.Programs;
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

// Never kills or closes anything; records the calls.
public sealed class FakeProcessEnder : IProcessEnder
{
    public List<RunningProcess> Running { get; } = [];
    public List<RunningProcess> Closed { get; } = [];
    public List<RunningProcess> Killed { get; } = [];
    public bool CloseEndsProcess { get; set; }

    public IReadOnlyList<RunningProcess> Find(string processName) =>
        Running.Where(p => string.Equals(p.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase), processName,
            StringComparison.OrdinalIgnoreCase)).ToList();

    public int CloseWindows(IReadOnlyList<RunningProcess> p)
    {
        Closed.AddRange(p);
        if (CloseEndsProcess) Running.RemoveAll(r => p.Contains(r));
        return p.Count;
    }

    public int Kill(IReadOnlyList<RunningProcess> p)
    {
        Killed.AddRange(p);
        Running.RemoveAll(r => p.Contains(r));
        return p.Count;
    }

    public bool AnyRunning(IReadOnlyList<RunningProcess> p) => p.Any(Running.Contains);
}

public sealed class FakeDiskSpace : IDiskSpace
{
    public DriveSpace? Space { get; set; } = new("C:\\", 200L * 1024 * 1024 * 1024, 500L * 1024 * 1024 * 1024);
    public DriveSpace? SystemDrive() => Space;
}

// In-memory package list; never touches the real packages.
public sealed class FakeAppPackages : IAppPackages
{
    readonly List<AppPackage> _installed = [];

    public List<string> Removed { get; } = [];
    public List<string> Registered { get; } = [];
    public string? RemoveError { get; set; }
    // Runs after a successful Remove (a test deletes the manifest here, like Windows does for Store-only apps).
    public Action<AppPackage>? AfterRemove { get; set; }

    public FakeAppPackages Add(AppPackage p)
    {
        _installed.Add(p);
        return this;
    }

    public IReadOnlyList<AppPackage> List() => _installed.ToList();

    public bool IsInstalled(string familyName) =>
        _installed.Any(p => string.Equals(p.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

    public void Remove(string fullName)
    {
        if (RemoveError is not null) throw new InvalidOperationException(RemoveError);
        var p = _installed.First(x => x.FullName == fullName);
        _installed.Remove(p);
        Removed.Add(fullName);
        AfterRemove?.Invoke(p);
    }

    public void Register(string manifestPath) => Registered.Add(manifestPath);

    public string ProgramFiles { get; set; } = @"C:\Program Files";

    public string? ManifestPathFor(string fullName) =>
        WinAppPackages.IsValidFullName(fullName) ? Path.Combine(ProgramFiles, "WindowsApps", fullName, "AppxManifest.xml") : null;

    // Test helper: the user installed it again.
    public void Reinstall(AppPackage p) => _installed.Add(p);
}
