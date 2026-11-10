using System.IO;
using System.Net.Http;
using System.Security.Principal;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Performance;
using WinWaechter.Core.Privacy;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.App.Services;

/// <summary>Composition root: wires Core services and view models with plain constructors.</summary>
public sealed class AppServices : IDisposable
{
    readonly IDisposable[] _owned;

    AppServices(AppEnvironment env, AppSettings settings, AblageJanitor janitor, ChangeGate gate, ShellViewModel shell,
        params IDisposable[] owned)
    {
        Env = env;
        Settings = settings;
        Janitor = janitor;
        Gate = gate;
        Shell = shell;
        _owned = owned;
    }

    /// <summary>Releases native handles (the PDH query of the live monitor).</summary>
    public void Dispose()
    {
        foreach (var d in _owned)
        {
            try { d.Dispose(); }
            catch (Exception ex) { FileLog.Warn("Freigeben beim Beenden fehlgeschlagen", ex); }
        }
    }

    public AppEnvironment Env { get; }
    public AppSettings Settings { get; }
    public AblageJanitor Janitor { get; }

    /// <summary>App-wide lock for system changes; background work (Ablage purge) uses it too.</summary>
    public ChangeGate Gate { get; }

    public ShellViewModel Shell { get; }

    public static AppServices Create(AppEnvironment env, AppSettings settings, IDialogService dialogs, ISystemShell shell, Action shutdown)
    {
        var user = new InteractiveUserContext();
        var reg = new WinRegistry(user);
        var sc = new WinServiceControl();
        var ts = new WinTaskStore();
        var fo = new WinFileOps();
        var sig = new WinTrustVerifier();
        var lnk = new WinShortcutResolver();
        var (rules, rulesInfo, updateRules) = LoadRules(CreateRulesStore(env, d => SecureDataDir.Ensure(d)), env.Version,
            UpdateHttp.CreateHandler());

        // elevated: only journals/sidecars owned by Administrators or SYSTEM may steer an undo
        var store = new SessionStore(env.DataRoot,
            env.IsElevated ? FileOwner.IsAdminOrSystem : null,
            env.IsElevated ? FileOwner.SetAdmins : null);
        var defender = new DefenderClient(new ProcessRunner(), reg);
        var firewall = new FwPolicy2Firewall();
        var factory = ChangeFactory.CreateDefault(reg, sc, ts, fo, rules);
        FirewallChange.Register(factory, firewall);
        DefenderRegistration.Register(factory, defender);
        var power = new WinPowerModes(reg);
        var animation = new WinAnimationSetting();
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";
        var hibernation = new WinHibernation(reg, new ProcessRunner(), windowsDir, Path.GetPathRoot(windowsDir) ?? @"C:\");
        var broadcast = new WinSettingBroadcast();
        PerformanceRegistration.Register(factory, reg, power, animation, hibernation, broadcast);

        var autostart = new AutostartScanner(reg, user, sig, rules, fo, lnk);
        var services = new ServiceScanner(sc, sig, rules, reg, fo);
        var tasks = new TaskScanner(ts, sig, rules, fo, user);
        var temp = new TempScanner(fo, user);
        var paths = CleanerPaths.For(user);
        var procs = new WinProcessList();
        var cache = new CacheScanner(fo, paths, procs, sc, reg);
        var leftovers = new LeftoverFolderScanner(fo, reg, rules, procs, sc, ts, user, lnk, LeftoverFolderScanner.DefaultRoots(paths));
        var restore = new RestorePointService();

        // the Downloads folder is resolved per scan, so a moved folder is picked up without a restart
        IReadOnlyList<Finding> Installers() => new InstallerScanner(fo, sig, DownloadsFolder.Resolve(reg, user)).Scan();

        var engine = new OptimizerEngine(
            new Func<IReadOnlyList<Finding>>[] { autostart.Scan, services.Scan, tasks.Scan, temp.Scan, cache.Scan, Installers, leftovers.Scan },
            store, factory, restore.TryCreate);

        var heuristics = new HeuristicScanner(new WmiProcessSource(), new TcpTableConnections(), sig, rules,
            user, fo, autostart, tasks, services);

        var canChange = env.IsElevated;
        var gate = new ChangeGate();
        var optimize = new OptimizeViewModel(engine, dialogs, canChange, gate);
        // the heuristic scanner continues synchronously after its first await, so keep it off the UI thread
        var protect = new ProtectViewModel(defender, (p, ct) => Task.Run(() => heuristics.ScanAsync(p, ct), ct),
            engine, ProcessTools.KillByPath, dialogs, canChange, gate);
        // every source runs on the thread pool with its own timeout; the whole check stays off the UI thread
        var securitySources = new SecuritySources(firewall, new WmiSecurityCenter(), defender, reg, new WmiVolumeEncryption(),
            new WuaUpdateSearch(), new TcpTableConnections(), procs, sig, new BrowserExtensionScanner(paths, reg));
        var security = new SecurityViewModel(ct => Task.Run(() => new SecurityCheck(securitySources).RunAsync(ct), ct),
            engine, defender, dialogs, shell, canChange, gate, () => protect.IsDefenderBusy);
        // the DISM probe and the registry reads stay off the UI thread
        var privacy = new PrivacyViewModel(ct => Task.Run<IReadOnlyList<PrivacyRow>>(async () =>
        {
            var recall = await RecallProbe.IsPresentAsync(new ProcessRunner(), ct);
            return new PrivacyScanner(reg, rules).Scan(r => r == "recall" && recall);
        }, ct), engine, dialogs, canChange, gate);
        // traces (services, tasks, autostarts) for the leftover search and the forced removal
        IReadOnlyList<Finding> TraceScan() => [.. autostart.Scan(), .. services.Scan(), .. tasks.Scan()];
        var programService = new ProgramService(new ProgramInventory(reg, rules), reg, fo, sig, new JobUninstallLauncher(user.Sid),
            new UninstallLeftoverFinder(reg, fo, rules, procs, paths, TraceScan),
            new ForcedRemovalPlanner(reg, fo, rules, procs, paths, TraceScan), paths);
        // the view model runs every read of the service on the thread pool
        var programs = new ProgramsViewModel(programService, engine, dialogs, shell, canChange, gate);
        // Leistung: the animation switch (SPI) acts on the process's own account, so it is offered only when
        // that is the signed-in user. The view model runs the scan, the samples and the boot report on the pool.
        var ownAccount = string.Equals(WindowsIdentity.GetCurrent().User?.Value, user.Sid, StringComparison.OrdinalIgnoreCase);
        var perfScanner = new PerformanceScanner(reg, power, animation, hibernation, new WinAppDisplayNames(reg), ownAccount, broadcast);
        var systemSources = new WinSystemSources();
        var bootSource = new WinBootEventSource();
        ShellViewModel? shellVm = null;
        var performance = new PerformanceViewModel(
            _ => Task.FromResult<(IReadOnlyList<PerfSetting>, Func<PerfSetting, int, IReadOnlyList<IChange>>)>(
                (perfScanner.Scan(), perfScanner.ChangesFor)),
            new SystemSampler(systemSources), () => BootReport.Load(bootSource, DateTime.UtcNow),
            engine, dialogs, shell, canChange, gate, () => shellVm?.Navigate(optimize), new DispatcherTickSource());
        var janitor = new AblageJanitor(store);
        var history = new HistoryViewModel(store, engine, janitor, dialogs, canChange, gate);
        var update = new UpdateViewModel(new UpdateService(env), dialogs, canChange, shutdown, gate);
        var settingsVm = new SettingsViewModel(settings, env, update, shell, rulesInfo, updateRules);

        shellVm = new ShellViewModel(optimize, performance, protect, security, privacy, programs, history, settingsVm, update,
            env.IsElevated);
        return new AppServices(env, settings, janitor, gate, shellVm, systemSources);
    }

    // Downloaded rules live in <data>\Regeln. Elevated, that folder is secured before the store reads from it.
    internal static RulesStore CreateRulesStore(AppEnvironment env, Action<string> secureDir)
    {
        if (env.IsElevated) secureDir(env.RulesDir);
        return new RulesStore(env.RulesDir, UpdateKeys.PublicKeyPem,
            env.IsElevated ? FileOwner.IsAdminOrSystem : null,
            env.IsElevated ? FileOwner.SetAdmins : null);
    }

    // Effective rules (bundled plus a verified newer cache) and one updater for them. The updater starts
    // from the effective version, so an older signed file can never replace a newer cache.
    internal static (RuleSet Rules, RulesInfo Info, Func<CancellationToken, Task<RulesUpdateResult>> Update) LoadRules(
        RulesStore store, SemVer app, HttpMessageHandler handler)
    {
        var (rules, info) = RulesLoader.Load(RuleSet.Default, store, app);
        FileLog.Info(info.DisplayText);
        var updater = new RulesUpdater(new RulesFetcher(handler), store, info.Version, app);
        return (rules, info, updater.RunAsync);
    }
}
