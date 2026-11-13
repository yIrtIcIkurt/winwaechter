using System.Text.RegularExpressions;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>
/// Offers browser caches and Windows leftovers (update downloads, crash dumps, error reports) for the Ablage.
/// Fixed targets only, no rule set. A target that is in use is shown as Info without a change.
/// The scanner never closes a browser or stops a service; a blocked browser cache names its process
/// (Finding.BlockingProcess), so the Optimize page can offer „Browser schließen und leeren“.
/// </summary>
public sealed class CacheScanner
{
    const string EdgeBlocked = "Edge läuft (auch im Hintergrund). Edge vollständig schließen und erneut prüfen.";
    const string BrowserBlocked = "Browser ist geöffnet – zum Aufräumen schließen";
    const string RebootPending = "Ein Neustart nach Updates steht aus – erst neu starten";
    const string UpdateBusy = "Windows Update arbeitet gerade";
    const string RebootKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    static readonly string[] ProfileCaches = ["Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    static readonly string[] RootCaches = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    static readonly Regex NumberedProfile = new(@"^Profile \d+$", RegexOptions.CultureInvariant);
    static readonly TimeSpan DumpAge = TimeSpan.FromDays(2);

    readonly IFileOps _files;
    readonly CleanerPaths _p;
    readonly IProcessList _procs;
    readonly IServiceControl _sc;
    readonly IRegistry _reg;

    public CacheScanner(IFileOps f, CleanerPaths paths, IProcessList procs, IServiceControl sc, IRegistry reg)
    {
        _files = f; _p = paths; _procs = procs; _sc = sc; _reg = reg;
    }

    sealed record Target(string Title, string Group, string Location, MoveToAblageChange? Change, int Age, string? Blocked,
        string? BlockingProcess = null);

    public IReadOnlyList<Finding> Scan()
    {
        var running = SafeSnapshot();
        var result = new List<Finding>();
        foreach (var t in Targets(running)) Add(result, t);
        return result;
    }

    IEnumerable<Target> Targets(IReadOnlyList<RunningProcess> running)
    {
        var local = _p.LocalAppData;
        var edgeRoot = Path.Combine(local, "Microsoft", "Edge", "User Data");
        yield return Browser("Browser-Cache Edge", edgeRoot, SafeIncludes(edgeRoot, () => ChromiumIncludes(edgeRoot)), running, "msedge.exe",
            [Path.Combine(_p.ProgramFilesX86, "Microsoft", "Edge", "Application"), Path.Combine(_p.ProgramFiles, "Microsoft", "Edge", "Application")],
            EdgeBlocked);

        var chromeRoot = Path.Combine(local, "Google", "Chrome", "User Data");
        yield return Browser("Browser-Cache Chrome", chromeRoot, SafeIncludes(chromeRoot, () => ChromiumIncludes(chromeRoot)), running, "chrome.exe",
            [Path.Combine(_p.ProgramFiles, "Google", "Chrome", "Application"), Path.Combine(_p.ProgramFilesX86, "Google", "Chrome", "Application"),
             Path.Combine(local, "Google", "Chrome", "Application")],
            BrowserBlocked);

        // Opera keeps caches in Local, shader caches in Roaming; both sit under the parent of LocalAppData.
        var operaLocal = Path.Combine(local, "Opera Software", "Opera Stable");
        var operaRoaming = Path.Combine(_p.RoamingAppData, "Opera Software", "Opera Stable");
        var opera = new List<string>();
        opera.AddRange(ProfileCaches.Select(n => Path.Combine(operaLocal, "Default", n)));
        opera.AddRange(new[] { "Cache", "Code Cache", "GPUCache" }.Select(n => Path.Combine(operaLocal, n)));
        opera.AddRange(new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" }.Select(n => Path.Combine(operaRoaming, n)));
        yield return Browser("Browser-Cache Opera", Path.GetDirectoryName(local.TrimEnd('\\')) ?? local,
            opera.Where(Directory.Exists).ToList(), running, "opera.exe",
            [Path.Combine(local, "Programs", "Opera"), Path.Combine(_p.ProgramFiles, "Opera")], BrowserBlocked);

        var ffRoot = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        var ff = SafeIncludes(ffRoot, () =>
            _files.EnumerateTopLevelDirs(ffRoot).Select(d => Path.Combine(d.FullName, "cache2")).Where(Directory.Exists).ToList());
        yield return Browser("Browser-Cache Firefox", ffRoot, ff, running, "firefox.exe",
            [Path.Combine(_p.ProgramFiles, "Mozilla Firefox"), Path.Combine(_p.ProgramFilesX86, "Mozilla Firefox")], BrowserBlocked);

        var wu = Path.Combine(_p.SystemRoot, "SoftwareDistribution", "Download");
        yield return Folder("Windows-Update-Downloads", wu, [wu], 7, ageByNewer: true, blocked: () => UpdateBlock(wu));

        var crash = Path.Combine(local, "CrashDumps");
        yield return Folder("Absturzabbilder (Programme)", crash, [crash], 2);
        yield return Folder("Absturzabbilder (Windows)", _p.SystemRoot,
            new[] { "Minidump", "LiveKernelReports" }.Select(n => Path.Combine(_p.SystemRoot, n)).Where(Directory.Exists).ToList(), 2);
        yield return MemoryDump();

        yield return Wer("Fehlerberichte (Windows)", Path.Combine(_p.ProgramData, "Microsoft", "Windows", "WER"));
        yield return Wer("Fehlerberichte (Benutzer)", Path.Combine(local, "Microsoft", "Windows", "WER"));
    }

    // ---- browsers ----

    // An unreadable browser folder skips only that browser, not the whole scan.
    static List<string> SafeIncludes(string root, Func<List<string>> build)
    {
        try { return build(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Warn($"Browser-Cache übersprungen, Ordner nicht lesbar: {root}", ex);
            return [];
        }
    }

    List<string> ChromiumIncludes(string root)
    {
        var includes = new List<string>();
        foreach (var d in _files.EnumerateTopLevelDirs(root))
        {
            var isProfile = d.Name == "Default" || NumberedProfile.IsMatch(d.Name) || File.Exists(Path.Combine(d.FullName, "Preferences"));
            if (isProfile) includes.AddRange(ProfileCaches.Select(n => Path.Combine(d.FullName, n)));
        }
        includes.AddRange(RootCaches.Select(n => Path.Combine(root, n)));
        return includes.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    Target Browser(string title, string root, List<string> includes, IReadOnlyList<RunningProcess> running,
        string exe, string[] installDirs, string blockedText)
    {
        var change = includes.Count == 0 ? null : MoveToAblageChange.ForCache(_files, root, includes, TimeSpan.Zero);
        // msedgewebview2.exe has another name and never counts; an unreadable path counts as running
        var isRunning = running.Any(r => r.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)
            && (r.ExePath is null || installDirs.Any(d => IsUnder(r.ExePath, d))));
        return new Target(title, FindingGroups.BrowserCache, root, change, 0, isRunning ? blockedText : null,
            isRunning ? Path.GetFileNameWithoutExtension(exe) : null);
    }

    static bool IsUnder(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // ---- Windows leftovers ----

    Target Folder(string title, string root, List<string> includes, int days, bool ageByNewer = false, Func<string?>? blocked = null)
    {
        if (includes.Count == 0) return new Target(title, FindingGroups.WindowsRemnants, root, null, days, null);
        var change = MoveToAblageChange.ForCache(_files, root, includes, TimeSpan.FromDays(days), ageByNewer);
        return new Target(title, FindingGroups.WindowsRemnants, root, change, days, blocked?.Invoke());
    }

    Target Wer(string title, string root) =>
        Folder(title, root, new[] { "ReportArchive", "ReportQueue" }.Select(n => Path.Combine(root, n)).Where(Directory.Exists).ToList(), 2);

    // MEMORY.DMP is one single file, offered only when older than 2 days.
    Target MemoryDump()
    {
        const string title = "Speicherabbild (MEMORY.DMP)";
        var path = Path.Combine(_p.SystemRoot, "MEMORY.DMP");
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.LastWriteTimeUtc < DateTime.UtcNow - DumpAge)
                return new Target(title, FindingGroups.WindowsRemnants, path, MoveToAblageChange.ForCacheFile(_files, path), 2, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable: not offered */ }
        return new Target(title, FindingGroups.WindowsRemnants, path, null, 2, null);
    }

    string? UpdateBlock(string folder)
    {
        if (_reg.KeyExists(Hive.LocalMachine, RebootKey)) return RebootPending;
        if (_reg.Get(Hive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations") is string[] pending
            && pending.Any(s => !string.IsNullOrWhiteSpace(s))) return RebootPending;
        if (ServiceRunning("TrustedInstaller")) return UpdateBusy;
        if (ServiceRunning("wuauserv") && HasFileNewerThan(folder, TimeSpan.FromHours(1))) return UpdateBusy;
        return null;
    }

    // An unreadable service state counts as busy: better to wait than to touch a running update.
    bool ServiceRunning(string service)
    {
        try { return _sc.Get(service)?.Running == true; }
        catch (Exception) { return true; }
    }

    static bool HasFileNewerThan(string folder, TimeSpan age)
    {
        var cutoff = DateTime.UtcNow - age;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true })
                .Any(f => f.LastWriteTimeUtc > cutoff || f.CreationTimeUtc > cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // ---- result ----

    // Fail closed: if the process list cannot be read, every browser counts as running.
    IReadOnlyList<RunningProcess> SafeSnapshot()
    {
        try { return _procs.Snapshot(); }
        catch (Exception)
        {
            return new[] { "msedge.exe", "chrome.exe", "opera.exe", "firefox.exe" }
                .Select(n => new RunningProcess(n, null)).ToList();
        }
    }

    static void Add(List<Finding> result, Target t)
    {
        if (t.Change is null) return;
        var (count, bytes) = t.Change.Measure();
        if (count == 0) return;
        var line = t.Age > 0 ? $"{count} Dateien älter als {t.Age} Tage" : $"{count} Dateien";
        // show what is really moved, not the common root
        var where = t.Change.MovedText;
        var reasons = new List<string> { line };
        reasons.AddRange(t.Change.IncludePaths);
        if (t.Blocked is null)
            result.Add(new Finding($"cache|{t.Title}", FindingArea.Speicher, t.Title, where, reasons,
                Rating.Entbehrlich, true, t.Change, bytes, Group: t.Group));
        else
        {
            reasons.Add(t.Blocked);
            result.Add(new Finding($"cache|{t.Title}", FindingArea.Speicher, t.Title, where, reasons,
                Rating.Info, false, null, bytes, Group: t.Group, BlockingProcess: t.BlockingProcess));
        }
    }
}
