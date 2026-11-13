using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Security.Checks;

/// <summary>Blocks inbound connections of one program with a Windows Firewall rule; Undo removes exactly that rule.</summary>
public sealed class FirewallRuleChange : IChange
{
    public const string Prefix = "WinWächter: ";
    const string Foreign = "Diese Firewall-Regel stammt nicht von WinWächter – Rückgängig abgelehnt.";

    readonly IFirewallRules _rules;
    readonly string _program;
    readonly string _name;
    readonly string _file;

    public FirewallRuleChange(IFirewallRules rules, string program, string windowsDir)
    {
        if (string.IsNullOrWhiteSpace(program) || !Path.IsPathRooted(program))
            throw new ArgumentException("Der Programmpfad muss vollständig angegeben sein.", nameof(program));
        if (program.Any(char.IsControl))
            throw new ArgumentException("Der Programmpfad enthält Steuerzeichen.", nameof(program));
        if (IsUnder(program, windowsDir))
            throw new ArgumentException("Programme im Windows-Ordner werden nicht gesperrt.", nameof(program));
        _rules = rules;
        _program = program;
        _file = Path.GetFileName(program);
        _name = RuleName(program);
    }

    public static string RuleName(string program) => Prefix + Path.GetFileNameWithoutExtension(program);

    static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public string Kind => "fwrule";
    public string Target => _name;
    public string Description => $"Eingehende Verbindungen für „{_file}“ sperren";

    public static FirewallRuleChange FromRecord(ChangeRecord r, IFirewallRules rules, string windowsDir)
    {
        var name = r.Before.GetValueOrDefault("name");
        var program = r.Before.GetValueOrDefault("program");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(program)) throw new InvalidOperationException(Foreign);
        try
        {
            if (name != RuleName(program)) throw new InvalidOperationException(Foreign);
            return new FirewallRuleChange(rules, program, windowsDir);
        }
        catch (ArgumentException) { throw new InvalidOperationException(Foreign); }
    }

    public static void Register(ChangeFactory f, IFirewallRules rules, string windowsDir) =>
        f.Register("fwrule", r => FromRecord(r, rules, windowsDir));

    public ChangeRecord Apply(SessionContext ctx)
    {
        if (_rules.Get(_name) is not null)
            throw new InvalidOperationException($"Eine Firewall-Regel „{_name}“ gibt es schon.");
        _rules.AddInboundBlock(_name, _program);
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["name"] = _name, ["program"] = _program }, new() { ["present"] = "true" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() =>
        (new() { ["name"] = _name, ["program"] = _program }, new() { ["present"] = "true" });

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        var live = _rules.Get(_name);
        if (live is null) return;
        if (!string.Equals(live.Program, _program, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Die Firewall-Regel „{_name}“ gehört nicht mehr zu diesem Programm – Rückgängig abgelehnt.");
        _rules.Remove(_name);
    }

    public DriftState CheckDrift(ChangeRecord record)
    {
        var live = _rules.Get(_name);
        if (live is null) return DriftState.Gone;
        return string.Equals(live.Program, _program, StringComparison.OrdinalIgnoreCase) ? DriftState.Unchanged : DriftState.Changed;
    }
}
