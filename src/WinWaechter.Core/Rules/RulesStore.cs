using System.Text;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.Core.Rules;

public enum RulesAcceptance { Accepted, BadSignature, Invalid, NotNewer, AppTooOld }

public sealed record CachedRules(RuleSet Rules, DateTime StoredAtUtc);

// Downloaded rules in %ProgramData%\WinWaechter\Regeln (rules.json + rules.json.sig).
// The cache is re-verified on every load: size, owner (Administrators/SYSTEM), signature, JSON.
public sealed class RulesStore
{
    public const long MaxBytes = 1024 * 1024;
    public const string JsonName = "rules.json", SigName = "rules.json.sig";
    const long MaxSigBytes = 1024;

    readonly string _publicKeyPem;
    readonly Func<string, bool> _isTrusted;
    readonly Action<string> _claimFile;

    // isTrusted defaults to FileOwner.IsAdminOrSystem, claimFile to FileOwner.SetAdmins (tests pass their own).
    public RulesStore(string dir, string publicKeyPem, Func<string, bool>? isTrusted = null, Action<string>? claimFile = null)
    {
        Dir = dir;
        _publicKeyPem = publicKeyPem;
        _isTrusted = isTrusted ?? FileOwner.IsAdminOrSystem;
        _claimFile = claimFile ?? FileOwner.SetAdmins;
    }

    public string Dir { get; }

    string JsonPath => Path.Combine(Dir, JsonName);
    string SigPath => Path.Combine(Dir, SigName);

    public CachedRules? LoadVerified()
    {
        try
        {
            if (!File.Exists(JsonPath) || !File.Exists(SigPath))
            {
                FileLog.Info("Keine heruntergeladenen Regeln vorhanden.");
                return null;
            }
            // Both handles stay open (no write/delete sharing) while owner and contents are checked.
            using var js = new FileStream(JsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ss = new FileStream(SigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (js.Length > MaxBytes || ss.Length > MaxSigBytes)
                return Reject("zu groß");
            if (!_isTrusted(JsonPath) || !_isTrusted(SigPath))
                return Reject("Besitzer nicht vertrauenswürdig");

            var json = new byte[js.Length];
            js.ReadExactly(json);
            var sigBytes = new byte[ss.Length];
            ss.ReadExactly(sigBytes);
            if (!RulesSignature.Verify(json, Encoding.ASCII.GetString(sigBytes), _publicKeyPem))
                return Reject("Signatur ungültig");

            RuleSet rules;
            try { rules = RuleSet.Parse(json); }
            catch (InvalidDataException e) { return Reject("fehlerhaft: " + e.Message); }
            return new CachedRules(rules, File.GetLastWriteTimeUtc(JsonPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileLog.Warn("Heruntergeladene Regeln konnten nicht gelesen werden", e);
            return null;
        }
    }

    static CachedRules? Reject(string why)
    {
        FileLog.Warn($"Heruntergeladene Regeln werden ignoriert ({why}).");
        return null;
    }

    // Writes both files only when everything checks out. IO errors propagate (the updater reports them).
    public RulesAcceptance TryAccept(byte[] json, string sigBase64, int currentVersion, SemVer app)
    {
        if (json.Length > MaxBytes) return RulesAcceptance.Invalid;
        if (!RulesSignature.Verify(json, sigBase64, _publicKeyPem)) return RulesAcceptance.BadSignature;

        RuleSet rules;
        try { rules = RuleSet.Parse(json); }
        catch (InvalidDataException) { return RulesAcceptance.Invalid; }

        if (rules.RulesVersion <= currentVersion) return RulesAcceptance.NotNewer;
        if (!SemVer.TryParse(rules.MinAppVersion, out var min)) return RulesAcceptance.Invalid;
        if (min > app) return RulesAcceptance.AppTooOld;

        Directory.CreateDirectory(Dir);
        var jsonTmp = JsonPath + ".tmp";
        var sigTmp = SigPath + ".tmp";
        try
        {
            File.WriteAllBytes(jsonTmp, json);
            File.WriteAllText(sigTmp, sigBase64.Trim(), Encoding.ASCII);
            File.Move(jsonTmp, JsonPath, overwrite: true);
            File.Move(sigTmp, SigPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(jsonTmp); } catch { }
            try { File.Delete(sigTmp); } catch { }
            throw;
        }
        _claimFile(JsonPath);
        _claimFile(SigPath);
        FileLog.Info($"Neue Regeln Version {rules.RulesVersion} gespeichert.");
        return RulesAcceptance.Accepted;
    }
}
