using System.Reflection;
using System.Runtime.InteropServices;

namespace WinWaechter.Core.Security.Checks;

public sealed record FirewallRuleInfo(string Name, string? Program, bool Inbound, bool Block, bool Enabled);

/// <summary>The Windows Firewall's rule list, reduced to what WinWächter needs.</summary>
public interface IFirewallRules
{
    /// <summary>The first rule with that name, or null.</summary>
    FirewallRuleInfo? Get(string name);

    /// <summary>Adds an enabled inbound block rule for one program, all profiles.</summary>
    void AddInboundBlock(string name, string program);

    void Remove(string name);
}

// HNetCfg.FwPolicy2 → Rules / HNetCfg.FWRule through late binding; every COM object is released.
public sealed class FwPolicy2Rules : IFirewallRules
{
    const int ErrorFileNotFound = unchecked((int)0x80070002);
    const int AllProfiles = 0x7FFFFFFF;
    const int DirectionIn = 1, ActionBlock = 0;
    public const string Group = "WinWächter";

    static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new InvalidOperationException("Firewall-Richtlinie nicht verfügbar.");

    static object? Read(object o, string prop) => o.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, o, null);
    static void Write(object o, string prop, object value) => o.GetType().InvokeMember(prop, BindingFlags.SetProperty, null, o, [value]);

    public FirewallRuleInfo? Get(string name)
    {
        var pol = Create("HNetCfg.FwPolicy2");
        object? rules = null, rule = null;
        try
        {
            rules = Read(pol, "Rules");
            try { rule = rules!.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, rules, [name]); }
            catch (TargetInvocationException ex) when (ex.InnerException is FileNotFoundException || ex.InnerException?.HResult == ErrorFileNotFound)
            {
                return null;
            }
            if (rule is null) return null;
            return new FirewallRuleInfo(name, Read(rule, "ApplicationName") as string,
                Convert.ToInt32(Read(rule, "Direction")) == DirectionIn,
                Convert.ToInt32(Read(rule, "Action")) == ActionBlock,
                Convert.ToBoolean(Read(rule, "Enabled")));
        }
        finally
        {
            if (rule is not null) Marshal.ReleaseComObject(rule);
            if (rules is not null) Marshal.ReleaseComObject(rules);
            Marshal.ReleaseComObject(pol);
        }
    }

    public void AddInboundBlock(string name, string program)
    {
        var pol = Create("HNetCfg.FwPolicy2");
        object? rules = null;
        var rule = Create("HNetCfg.FWRule");
        try
        {
            Write(rule, "Name", name);
            Write(rule, "Direction", DirectionIn);
            Write(rule, "Action", ActionBlock);
            Write(rule, "Enabled", true);
            Write(rule, "Profiles", AllProfiles);
            Write(rule, "ApplicationName", program);
            Write(rule, "Grouping", Group);
            rules = Read(pol, "Rules");
            rules!.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, rules, [rule]);
        }
        finally
        {
            if (rules is not null) Marshal.ReleaseComObject(rules);
            Marshal.ReleaseComObject(rule);
            Marshal.ReleaseComObject(pol);
        }
    }

    public void Remove(string name)
    {
        var pol = Create("HNetCfg.FwPolicy2");
        object? rules = null;
        try
        {
            rules = Read(pol, "Rules");
            rules!.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, rules, [name]);
        }
        finally
        {
            if (rules is not null) Marshal.ReleaseComObject(rules);
            Marshal.ReleaseComObject(pol);
        }
    }
}
