using System.Globalization;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Heuristics;

/// <summary>
/// Own miner/suspicion heuristics next to Defender: running processes, network connections and
/// persistence entries are scored per executable path. Everything is offered, nothing is preselected
/// unless the score says Verdaechtig.
/// </summary>
public sealed class HeuristicScanner
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    sealed class Acc(string path)
    {
        public string Path { get; } = path;
        public double Cpu;
        public bool Load, Pool, MinerCmd, Hidden;
        public List<int> Ports { get; } = [];
        public Finding? Task, Autostart, Service;
        public bool HasSource => Task is not null || Autostart is not null || Service is not null;
    }

    readonly IProcessSource _procs;
    readonly IConnectionSource _conns;
    readonly ISignatureVerifier _sig;
    readonly RuleSet _rules;
    readonly IUserContext _user;
    readonly IFileOps _files;
    readonly AutostartScanner _autostart;
    readonly TaskScanner _tasks;
    readonly ServiceScanner _services;
    readonly TimeSpan _window;
    readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    readonly string[] _unusualDirs;
    readonly string _programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public HeuristicScanner(IProcessSource p, IConnectionSource c, ISignatureVerifier s, RuleSet rules,
        IUserContext user, IFileOps files, AutostartScanner a, TaskScanner t, ServiceScanner sv, TimeSpan? window = null)
    {
        _procs = p; _conns = c; _sig = s; _rules = rules; _user = user; _files = files;
        _autostart = a; _tasks = t; _services = sv; _window = window ?? DefaultWindow;
        var publicDir = Environment.GetEnvironmentVariable("PUBLIC");
        _unusualDirs = new[]
        {
            Path.Combine(user.ProfileDir, "AppData", "Local"), Path.Combine(user.ProfileDir, "AppData", "Roaming"),
            user.TempDir, Path.Combine(_windows, "Temp"),
            string.IsNullOrEmpty(publicDir) ? @"C:\Users\Public" : publicDir,
        }.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.TrimEnd('\\')).ToArray();
    }

    public async Task<IReadOnlyList<Finding>> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var map = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        Acc Get(string path) => map.TryGetValue(path, out var a) ? a : map[path] = new Acc(path);

        progress?.Report("Laufende Programme werden beobachtet …");
        var samples = await _procs.SampleAsync(_window, ct);
        ct.ThrowIfCancellationRequested();

        progress?.Report("Netzwerkverbindungen werden geprüft …");
        var conns = SafeConnections();
        var cmdByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pidPath = new Dictionary<int, string>();
        foreach (var s in samples)
        {
            if (string.IsNullOrWhiteSpace(s.ExePath)) continue;
            var path = PathNormalizer.NormalizePath(s.ExePath);
            pidPath[s.Pid] = path;
            var acc = Get(path);
            acc.Cpu = Math.Max(acc.Cpu, s.CpuPercentAvg);
            if (s.SustainedHigh) acc.Load = true;
            if (!string.IsNullOrEmpty(s.CommandLine) && _rules.IsMinerCommandLine(s.CommandLine)) acc.MinerCmd = true;
        }
        foreach (var c in conns)
            if (_rules.PoolPorts.Contains(c.RemotePort) && pidPath.TryGetValue(c.Pid, out var p))
            {
                var acc = Get(p);
                acc.Pool = true;
                if (!acc.Ports.Contains(c.RemotePort)) acc.Ports.Add(c.RemotePort);
            }

        progress?.Report("Autostarts, Aufgaben und Dienste werden geprüft …");
        ct.ThrowIfCancellationRequested();
        AddSources(map, Safe(_autostart.Scan), (a, f) => a.Autostart ??= f);
        AddSources(map, Safe(_tasks.Scan), (a, f) => a.Task ??= f);
        AddSources(map, Safe(_services.Scan), (a, f) => a.Service ??= f);

        progress?.Report("Auffälligkeiten werden bewertet …");
        var result = new List<Finding>();
        foreach (var acc in map.Values)
        {
            ct.ThrowIfCancellationRequested();
            var f = Evaluate(acc);
            if (f is not null) result.Add(f);
        }
        return result.OrderByDescending(f => f.Rating).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    void AddSources(Dictionary<string, Acc> map, IReadOnlyList<Finding> findings, Action<Acc, Finding> set)
    {
        foreach (var f in findings)
        {
            var raw = CommandLineParser.ExtractExePath(f.Location);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = PathNormalizer.NormalizePath(raw);
            if (!Path.IsPathRooted(path)) continue;
            if (!map.TryGetValue(path, out var acc)) map[path] = acc = new Acc(path);
            set(acc, f);
            if (f.Area == FindingArea.Aufgabe && f.Reasons.Contains("versteckt")) acc.Hidden = true;
            if (_rules.IsMinerCommandLine(f.Location)) acc.MinerCmd = true;
        }
    }

    Finding? Evaluate(Acc acc)
    {
        var path = acc.Path;
        if (IsUnder(path, _windows)) return null;                      // never proposed

        var file = Path.GetFileName(path);
        var nameHit = IsMinerFileName(file);
        var unusual = IsUnusualLocation(path);
        if (!(acc.Load || acc.Pool || acc.MinerCmd || acc.Hidden || nameHit || unusual)) return null;

        SignatureInfo sig;
        try { sig = _sig.Verify(path); } catch { sig = new SignatureInfo(SignatureState.Unknown, null, false); }
        if (sig.State == SignatureState.Valid && sig.IsMicrosoft) return null;

        var verdict = _rules.Classify(new ItemFacts(Path.GetFileNameWithoutExtension(path), path, sig.Publisher, null));
        var badSignature = sig.State is SignatureState.Unsigned or SignatureState.Invalid;
        if (verdict == RuleVerdict.Protected && sig.State == SignatureState.Valid) return null;

        var reasons = new List<string>();
        var points = 0;
        void Add(int p, string reason) { points += p; reasons.Add(reason); }

        if (verdict == RuleVerdict.Protected)
        {
            if (badSignature) reasons.Add("gibt sich als geschützter Hersteller aus, ist aber nicht gültig signiert");
            else reasons.Add("Hersteller geschützt, Signatur nicht prüfbar");
        }
        if (acc.Load)
            Add(Scoring.Load, string.Create(CultureInfo.GetCultureInfo("de-DE"), $"Anhaltend hohe CPU-Last (ca. {acc.Cpu:0} %)"));
        if (unusual)
        {
            Add(Scoring.UnusualPlace, "Programm liegt an einem ungewöhnlichen Ort (AppData, Temp, ProgramData oder Öffentlich)");
            if (badSignature) Add(Scoring.UnsignedInPlace, "Programmdatei ist nicht gültig signiert");
        }
        if (nameHit) Add(Scoring.MinerName, "Programmname entspricht einem bekannten Kryptominer");
        if (acc.MinerCmd) Add(Scoring.MinerCommandLine, "Kommandozeile entspricht einem Muster für Kryptominer");
        if (acc.Pool) Add(Scoring.PoolPort, $"Verbindung zu typischem Mining-Pool-Port ({string.Join(", ", acc.Ports.Order())})");
        if (acc.Hidden) Add(Scoring.HiddenTask, "Wird von einer versteckten Aufgabe gestartet");
        if (acc.HasSource)
        {
            reasons.Add("Wird automatisch gestartet (Autostart, Aufgabe oder Dienst)");
            if (unusual && badSignature) points += Scoring.PersistUnsignedInPlace;
            if (unusual && badSignature) reasons.Add("Automatischer Start einer nicht signierten Datei aus AppData/Temp");
        }
        if (verdict == RuleVerdict.Pua) Add(Scoring.Pua, "Laut Regelliste unerwünschte Software");

        if (points == 0) return null;
        var rating = Scoring.ToRating(points);
        var protectedByName = verdict == RuleVerdict.Protected;
        var onlyConnection = acc.Pool && points == Scoring.PoolPort;

        // protected vendors are only shown: no change, never preselected, Defender check only
        return new Finding($"heur|{path}", onlyConnection ? FindingArea.Verbindung : FindingArea.Prozess, file, path,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), rating,
            rating == Rating.Verdaechtig && !protectedByName, protectedByName ? null : PickChange(acc),
            Publisher: sig.Publisher, IsProtectedVendor: protectedByName);
    }

    // Prefer the reversible change of a task, then autostart, then service; else move the file to the Ablage.
    IChange? PickChange(Acc acc)
    {
        var existing = acc.Task?.Change ?? acc.Autostart?.Change ?? acc.Service?.Change;
        if (existing is not null) return existing;
        try { return _files.Exists(acc.Path) ? MoveToAblageChange.ForFile(_files, acc.Path) : null; }
        catch { return null; }
    }

    // Exact file name or name followed by "-"/"_" (so "t-rex.exe" hits, "t-rexplorer.exe" does not).
    bool IsMinerFileName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var n in _rules.MinerNames)
        {
            if (stem.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            if (stem.Length > n.Length && stem.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                && stem[n.Length] is '-' or '_') return true;
        }
        return false;
    }

    bool IsUnusualLocation(string path)
    {
        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return true;
        if (_unusualDirs.Any(d => IsUnder(path, d))) return true;
        // ProgramData counts only outside protected vendor folders.
        return IsUnder(path, _programData)
            && _rules.Classify(new ItemFacts(Path.GetFileNameWithoutExtension(path), path, null, null)) != RuleVerdict.Protected;
    }

    static bool IsUnder(string path, string dir) =>
        !string.IsNullOrEmpty(dir) && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    IReadOnlyList<TcpConn> SafeConnections()
    {
        try { return _conns.Established(); }
        catch (Exception ex) { FileLog.Warn("Verbindungen nicht lesbar", ex); return []; }
    }

    static IReadOnlyList<Finding> Safe(Func<IReadOnlyList<Finding>> scan)
    {
        try { return scan(); }
        catch (Exception ex) { FileLog.Warn("Scanner für Heuristik fehlgeschlagen", ex); return []; }
    }
}
