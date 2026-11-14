using WinWaechter.Core.Changes;
using WinWaechter.Core.Optimizer.ContextMenu;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer;

/// <summary>
/// The kinds whose first offer has a protection check beyond FromRecord's shape check: before „Erneut versuchen“
/// the same check runs again on the live system (fwrule: Microsoft, WinWächter itself, protected vendors;
/// shellext: the entry must still be offered by a fresh scan).
/// </summary>
public static class RetryGuards
{
    public const string FirewallRefused =
        "Erneut versuchen abgelehnt: Dieses Programm wird nicht mehr gesperrt (Microsoft, WinWächter selbst oder ein geschützter Hersteller).";
    public const string ShellExtRefused =
        "Erneut versuchen abgelehnt: Der Eintrag wird nicht mehr angeboten (geschützt, schon abgeschaltet oder nicht mehr vorhanden).";
    public const string CheckFailed =
        "Erneut versuchen abgelehnt: Die Schutzprüfung ließ sich nicht durchführen.";

    public static IReadOnlyDictionary<string, Func<IChange, string?>> Create(ShellExtScanner shellExt, ISignatureVerifier sig,
        RuleSet rules, string ownDir) => new Dictionary<string, Func<IChange, string?>>(StringComparer.Ordinal)
    {
        ["fwrule"] = c => c is FirewallRuleChange fw && SecurityChecks.MayBlock(fw.Program, Verify(sig, fw.Program), rules, ownDir)
            ? null : FirewallRefused,
        ["shellext"] = c => shellExt.StillOffered(c) ? null : ShellExtRefused,
    };

    // like the listener row: an unreadable signature counts as unknown (the path and name checks still apply)
    static SignatureInfo Verify(ISignatureVerifier sig, string path)
    {
        try { return sig.Verify(path); }
        catch (Exception) { return new(SignatureState.Unknown, null, false); }
    }
}
