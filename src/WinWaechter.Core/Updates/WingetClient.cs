using WinWaechter.Core.Logging;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Updates;

/// <summary>One available update. UnknownVersion: winget cannot tell the installed version („&lt; x“, „Unknown“).
/// UserScope: installed for the signed-in user only (updated in that user's context).</summary>
public sealed record WingetPackage(string Name, string Id, string Installed, string Available, bool UnknownVersion, bool UserScope);

public enum WingetListStatus { Ok, Missing, NeedsSourceAgreement, Failed }

public sealed record WingetList(WingetListStatus Status, IReadOnlyList<WingetPackage> Packages, string? Error);

public interface IWingetClient
{
    Task<WingetList> ListUpgradesAsync(bool acceptSourceAgreements, CancellationToken ct);

    /// <summary>Updates one package; returns winget's exit code. Throws ArgumentException for an invalid id.</summary>
    Task<int> UpgradeAsync(WingetPackage p, Action<string>? onLine, CancellationToken ct);
}

/// <summary>winget.exe with fixed arguments (source „winget“ only). wingetPath null = winget missing.</summary>
public sealed class WingetClient(IProcessRunner runner, IUserProcessRunner userRunner, string? wingetPath, string userTempDir)
    : IWingetClient
{
    internal const int NoPackagesFound = unchecked((int)0x8A150014);
    internal const int SourceAgreementsNotAccepted = unchecked((int)0x8A150046);

    const string ListArgs = "upgrade --source winget --include-unknown --disable-interactivity";

    // winget writes UTF-8 to a pipe; the default in a window app would be the ANSI code page (names shift, IDs break)
    static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false);

    /// <summary>The App Installer alias of the signed-in user; null when it does not exist.</summary>
    public static string? FindWinget(string localAppData)
    {
        var p = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(p) ? p : null;
    }

    public async Task<WingetList> ListUpgradesAsync(bool acceptSourceAgreements, CancellationToken ct)
    {
        if (wingetPath is null) return new WingetList(WingetListStatus.Missing, [], null);
        var accept = acceptSourceAgreements ? " --accept-source-agreements" : "";

        var (exit, stdout, _) = await runner.RunAsync(wingetPath, ListArgs + accept, null, Utf8, ct).ConfigureAwait(false);
        if (exit == SourceAgreementsNotAccepted || (exit != 0 && stdout.Contains("--accept-source-agreements", StringComparison.Ordinal)))
            return new WingetList(WingetListStatus.NeedsSourceAgreement, [], null);
        var rows = WingetTable.Parse(stdout, d => FileLog.Info("winget: Zeile verworfen: " + d));
        if (exit != 0 && exit != NoPackagesFound && rows.Count == 0)
            return new WingetList(WingetListStatus.Failed, [], $"winget meldet einen Fehler (Code 0x{exit:X8}).");

        var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userRows = new List<(string Name, string Id, string Version, string Available, string Source)>();
        try
        {
            var (uExit, uOut, _) = await runner.RunAsync(wingetPath, ListArgs + " --scope user" + accept, null, Utf8, ct).ConfigureAwait(false);
            if (uExit == 0 || uExit == NoPackagesFound)
                foreach (var r in WingetTable.Parse(uOut))
                {
                    userIds.Add(r.Id);
                    userRows.Add(r);
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Warn("winget: Benutzerbereich nicht lesbar", ex);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<WingetPackage>();
        foreach (var r in rows.Concat(userRows))
            if (seen.Add(r.Id)) list.Add(new WingetPackage(r.Name, r.Id, r.Version, r.Available, IsUnknown(r.Version), userIds.Contains(r.Id)));
        return new WingetList(WingetListStatus.Ok, list, null);
    }

    static bool IsUnknown(string v) =>
        v.StartsWith('<') || v.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || v.Equals("Unbekannt", StringComparison.OrdinalIgnoreCase);

    public async Task<int> UpgradeAsync(WingetPackage p, Action<string>? onLine, CancellationToken ct)
    {
        if (!WingetTable.IsValidId(p.Id)) throw new ArgumentException($"Ungültige Paket-ID: {p.Id}", nameof(p));
        if (wingetPath is null) throw new InvalidOperationException("winget ist nicht installiert.");
        var args = $"upgrade --id {p.Id} --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements";
        if (!p.UserScope)
            return (await runner.RunAsync(wingetPath, args, onLine, Utf8, ct).ConfigureAwait(false)).Exit;

        // in the user's context there is no output pipe: winget writes a log the elevated app reads afterwards
        var log = Path.Combine(userTempDir, $"winwaechter-winget-{Guid.NewGuid():N}.log");
        try
        {
            var code = await userRunner.RunAsUserAsync(wingetPath, $"{args} --log \"{log}\"", ct).ConfigureAwait(false);
            if (onLine is not null && File.Exists(log))
                foreach (var line in ProcessRunner.SplitLines(await File.ReadAllTextAsync(log, ct).ConfigureAwait(false))) onLine(line);
            return code;
        }
        finally
        {
            try { File.Delete(log); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { FileLog.Warn("winget-Protokoll nicht löschbar", ex); }
        }
    }
}
