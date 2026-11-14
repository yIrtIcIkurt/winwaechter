using WinWaechter.Core.Changes;
using WinWaechter.Core.Optimizer.ContextMenu;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer;

/// <summary>
/// The kinds whose first offer has a protection check beyond FromRecord's shape check: before „Erneut versuchen“
/// the same check runs again on the live system (fwrule: Microsoft, WinWächter itself, protected vendors;
/// shellext: the entry must still be offered by a fresh scan; regvalue/perfvalue/secvalue: a present policy value
/// someone else set is never overwritten).
/// </summary>
public static class RetryGuards
{
    public const string FirewallRefused =
        "Erneut versuchen abgelehnt: WinWächter sperrt dieses Programm nicht (Microsoft, WinWächter selbst oder ein geschützter Hersteller).";
    public const string ShellExtRefused =
        "Erneut versuchen abgelehnt: Der Eintrag wird nicht mehr angeboten (geschützt, schon abgeschaltet oder nicht mehr vorhanden).";
    public const string PolicyRefused =
        "Erneut versuchen abgelehnt: Dieser Wert ist von einer Richtlinie festgelegt.";
    public const string CheckFailed =
        "Erneut versuchen abgelehnt: Die Schutzprüfung ließ sich nicht durchführen.";

    /// <param name="policies">Who set a present policy value (read fresh for every retry).</param>
    public static IReadOnlyDictionary<string, Func<IChange, string?>> Create(ShellExtScanner shellExt, ISignatureVerifier sig,
        RuleSet rules, string ownDir, IRegistry reg, Func<IPolicyOwnership> policies)
    {
        string? Policy(IChange c) => IsForeignPolicy(c.Target, reg, policies) ? PolicyRefused : null;
        return new Dictionary<string, Func<IChange, string?>>(StringComparer.Ordinal)
        {
            ["fwrule"] = c => c is FirewallRuleChange fw && SecurityChecks.MayBlock(fw.Program, Verify(sig, fw.Program), rules, ownDir)
                ? null : FirewallRefused,
            ["shellext"] = c => shellExt.StillOffered(c) ? null : ShellExtRefused,
            ["regvalue"] = Policy,
            ["perfvalue"] = Policy,
            ["secvalue"] = Policy,
        };
    }

    // Target is „HKCU|HKLM\key\name“ for the value kinds; anything else cannot be checked and is refused.
    static bool IsForeignPolicy(string target, IRegistry reg, Func<IPolicyOwnership> policies)
    {
        var first = target.IndexOf('\\');
        var last = target.LastIndexOf('\\');
        if (first <= 0 || last <= first) throw new InvalidOperationException($"Ziel nicht lesbar: {target}");
        Hive hive = target[..first] switch
        {
            "HKCU" => Hive.CurrentUser,
            "HKLM" => Hive.LocalMachine,
            _ => throw new InvalidOperationException($"Ziel nicht lesbar: {target}"),
        };
        var key = target[(first + 1)..last];
        var name = target[(last + 1)..];
        // the newest journal record of this value is the failed one, so a present policy value counts as foreign
        return PolicyValues.State(reg, policies(), hive, key, name) == PolicyState.Foreign;
    }

    // like the listener row: an unreadable signature counts as unknown (the path and name checks still apply)
    static SignatureInfo Verify(ISignatureVerifier sig, string path)
    {
        try { return sig.Verify(path); }
        catch (Exception) { return new(SignatureState.Unknown, null, false); }
    }
}
