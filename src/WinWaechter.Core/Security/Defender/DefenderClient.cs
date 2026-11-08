using System.Management;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Defender;

internal static class MpCmdRunArgs
{
    public const string Cancel = "-Scan -Cancel";
    public const string Update = "-SignatureUpdate";

    public static string Scan(ScanKind kind) => $"-Scan -ScanType {(int)kind}";
    public static string ScanPath(string path) => $"-Scan -ScanType 3 -File \"{path}\" -DisableRemediation";
}

/// <summary>Talks to Microsoft Defender via WMI, MpCmdRun and PowerShell. No own signatures.</summary>
public sealed class DefenderClient(IProcessRunner runner, IRegistry reg) : IDefender
{
    const string Scope = @"root\Microsoft\Windows\Defender";
    const string NotActiveNote = "Defender ist nicht aktiv – möglicherweise ist ein anderer Virenschutz installiert.";
    const string PowerShell = "powershell.exe";

    internal sealed record ThreatRow(long Id, string Name, int Severity, bool IsActive);
    internal sealed record DetectionRow(long ThreatId, int StatusId, DateTimeOffset? Time, IReadOnlyList<string> Resources);

    // ---- pure helpers ----

    internal static string StatusText(int id) => id switch
    {
        0 => "Unbekannt",
        1 => "Erkannt",
        2 => "Bereinigt",
        3 => "In Quarantäne",
        4 => "Entfernt",
        5 => "Zugelassen",
        6 => "Blockiert",
        102 => "Quarantäne fehlgeschlagen",
        103 => "Entfernen fehlgeschlagen",
        104 => "Zulassen fehlgeschlagen",
        105 => "Abgebrochen",
        107 => "Blockieren fehlgeschlagen",
        _ => $"Status {id}",
    };

    // Joins threats with their detections on ThreatID; the latest detection gives the status text.
    internal static IReadOnlyList<DefenderThreat> JoinThreats(IEnumerable<ThreatRow> threats, IEnumerable<DetectionRow> detections)
    {
        var byThreat = detections.GroupBy(d => d.ThreatId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<DefenderThreat>();
        foreach (var t in threats)
        {
            var list = byThreat.GetValueOrDefault(t.Id) ?? [];
            var latest = list.OrderByDescending(d => d.Time ?? DateTimeOffset.MinValue).FirstOrDefault();
            var resources = list.SelectMany(d => d.Resources).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add(new DefenderThreat(t.Id, t.Name, t.Severity, resources, t.IsActive, StatusText(latest?.StatusId ?? 0)));
        }
        return result;
    }

    // ---- WMI ----

    public Task<DefenderStatus> GetStatusAsync() => Task.Run(() =>
    {
        try
        {
            var cs = QueryFirst("SELECT * FROM MSFT_MpComputerStatus");
            if (cs is null || !AsBool(cs["AMServiceEnabled"]))
                return Unavailable();
            var pref = QueryFirst("SELECT * FROM MSFT_MpPreference");
            var pua = pref?["PUAProtection"] is { } v ? Convert.ToInt32(v) : 0;
            return new DefenderStatus(true, AsBool(cs["RealTimeProtectionEnabled"]), AsBool(cs["IsTamperProtected"]), pua,
                AsDate(cs["AntivirusSignatureLastUpdated"]), cs["AntivirusSignatureAge"] is { } a ? Convert.ToInt32(a) : 0,
                AsDate(cs["QuickScanEndTime"]), AsDate(cs["FullScanEndTime"]), null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            FileLog.Warn("Defender-Status nicht lesbar", ex);
            return Unavailable();
        }
    });

    static DefenderStatus Unavailable() => new(false, false, false, 0, null, 0, null, null, NotActiveNote);

    public Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync() => Task.Run<IReadOnlyList<DefenderThreat>>(() =>
    {
        try
        {
            var threats = Query("SELECT * FROM MSFT_MpThreat")
                .Select(o => new ThreatRow(Convert.ToInt64(o["ThreatID"]), o["ThreatName"] as string ?? "", Convert.ToInt32(o["SeverityID"] ?? 0), AsBool(o["IsActive"])))
                .ToList();
            var detections = Query("SELECT * FROM MSFT_MpThreatDetection")
                .Select(o => new DetectionRow(Convert.ToInt64(o["ThreatID"]), Convert.ToInt32(o["ThreatStatusID"] ?? 0),
                    AsDate(o["LastThreatStatusChangeTime"]) ?? AsDate(o["InitialDetectionTime"]),
                    o["Resources"] as string[] ?? []))
                .ToList();
            return JoinThreats(threats, detections);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            FileLog.Warn("Defender-Bedrohungen nicht lesbar", ex);
            return [];
        }
    });

    static List<ManagementBaseObject> Query(string wql)
    {
        using var s = new ManagementObjectSearcher(Scope, wql);
        return s.Get().Cast<ManagementBaseObject>().ToList();
    }

    static ManagementBaseObject? QueryFirst(string wql) => Query(wql).FirstOrDefault();

    static bool AsBool(object? o) => o is bool b && b;

    static DateTimeOffset? AsDate(object? o)
    {
        if (o is not string s || s.Length == 0) return null;
        var d = ManagementDateTimeConverter.ToDateTime(s);
        return d.Year < 1990 ? null : new DateTimeOffset(d);
    }

    // ---- MpCmdRun ----

    string MpCmdRunPath()
    {
        var dir = reg.Get(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender", "InstallLocation") as string;
        if (!string.IsNullOrWhiteSpace(dir)) return Path.Combine(dir, "MpCmdRun.exe");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
    }

    public Task<int> UpdateSignaturesAsync(CancellationToken ct) => RunMp(MpCmdRunArgs.Update, ct);

    public Task<int> ScanAsync(ScanKind kind, CancellationToken ct) => RunMp(MpCmdRunArgs.Scan(kind), ct);

    public Task<int> ScanPathAsync(string path, CancellationToken ct) => RunMp(MpCmdRunArgs.ScanPath(path), ct);

    // The process itself is never killed: cancelling asks Defender to stop, then we wait for the process to exit.
    async Task<int> RunMp(string args, CancellationToken ct)
    {
        var exe = MpCmdRunPath();
        var run = runner.RunAsync(exe, args, CancellationToken.None);
        var cancelled = new TaskCompletionSource();
        using var registration = ct.Register(() => cancelled.TrySetResult());
        if (await Task.WhenAny(run, cancelled.Task).ConfigureAwait(false) == run)
            return (await run.ConfigureAwait(false)).Exit;

        await runner.RunAsync(exe, MpCmdRunArgs.Cancel, CancellationToken.None).ConfigureAwait(false);
        await run.ConfigureAwait(false);
        throw new OperationCanceledException(ct);
    }

    // ---- PowerShell ----

    public async Task<int> RemoveActiveThreatsAsync()
    {
        var before = (await GetThreatsAsync().ConfigureAwait(false)).Count(t => t.Active);
        await RunPs("Remove-MpThreat").ConfigureAwait(false);
        return before;
    }

    public Task SetPuaModeAsync(int mode)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode), "PUA-Modus muss 0, 1 oder 2 sein.");
        return RunPs($"Set-MpPreference -PUAProtection {mode}");
    }

    async Task RunPs(string command)
    {
        var (exit, _, err) = await runner.RunAsync(PowerShell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"", CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException($"Defender-Befehl fehlgeschlagen (Code {exit}): {err.Trim()}");
    }
}
