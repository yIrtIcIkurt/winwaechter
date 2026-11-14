using System.IO;
using System.Net.Http;
using System.Security.Principal;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.ContextMenu;
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
        // Programme: preinstalled apps (Windows PowerShell Appx cmdlets for the signed-in user's packages)
        var pkgs = new WinAppPackages(new ProcessRunner(), user.Sid, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AppxChange.Register(factory, pkgs, rules);
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w : @"C:\Windows";
        // Sicherheit: secvalue, fwrule and sysprotect fixes (undo from the Verlauf needs them registered)
        SecValueChange.Register(factory, reg);
        var fwRules = new FwPolicy2Rules();
        FirewallRuleChange.Register(factory, fwRules, windowsDir);
        var restoreConfig = new WinSystemRestoreConfig(reg);
        SystemProtectChange.Register(factory, sc, restoreConfig);
        var power = new WinPowerModes();
        var animation = new WinAnimationSetting();
        var hibernation = new WinHibernation(reg, new ProcessRunner(), windowsDir, Path.GetPathRoot(windowsDir) ?? @"C:\");
        var broadcast = new WinSettingBroadcast();
        // one plan source for the undo registration and the scanner's „Energiesparplan“ row
        var plans = new WinPowerPlans();
        PerformanceRegistration.Register(factory, reg, power, animation, hibernation, broadcast, plans);
        ShellExtChange.Register(factory, reg);

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

        var shellExt = new ShellExtScanner(reg, sig, rules, windowsDir, AppContext.BaseDirectory);
        var engine = new OptimizerEngine(
            new Func<IReadOnlyList<Finding>>[] { autostart.Scan, services.Scan, tasks.Scan, temp.Scan, cache.Scan, Installers, leftovers.Scan, shellExt.Scan },
            store, factory, restore.TryCreate,
            // „Erneut versuchen“ (Verlauf): the protection check of the first offer runs again
            RetryGuards.Create(shellExt, sig, rules, AppContext.BaseDirectory));

        var heuristics = new HeuristicScanner(new WmiProcessSource(), new TcpTableConnections(), sig, rules,
            user, fo, autostart, tasks, services);

        var canChange = env.IsElevated;
        var gate = new ChangeGate();
        // one runner for every one-off action, on every page
        var actions = new ActionRunner(dialogs, gate);
        // Windows restarts the shell itself after the kill; the elevated app never launches Explorer
        var restartExplorer = SystemActions.RestartExplorer(new WinProcessEnder(procs), windowsDir);
        // „Beenden“ (Leistung) asks the guard at run time: never Windows, services, protected vendors (rules.json) or WinWächter
        // itself. The guard takes the raw service image paths; a failing protection check counts as protected.
        ProcessGuardContext PerfGuard()
        {
            var images = sc.List().Select(s => s.ImagePath).OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ProcessGuardContext(windowsDir, Environment.ProcessId, Environment.ProcessPath, images,
                p => rules.Classify(new ItemFacts(p.Name, p.ExePath, p.ExePath is { } exe ? sig.Verify(exe).Publisher : null, null))
                     == RuleVerdict.Protected);
        }
        // „Browser schließen und leeren“: the same guard, but only for the fixed browser list (ProcessGuard.MayEndBrowser)
        var optimize = new OptimizeViewModel(engine, dialogs, canChange, gate, actions, restartExplorer,
            new WinProcessEnder(procs), PerfGuard);
        // the same real-time switch on Schutz and Sicherheit
        var securityActions = SecurityActionSet.Create(defender, new WuaUpdateInstaller(), new WinRestarter(new ProcessRunner(), windowsDir));
        // the heuristic scanner continues synchronously after its first await, so keep it off the UI thread
        Task<IReadOnlyList<Finding>> HeuristicScan(IProgress<string>? p, CancellationToken ct) =>
            Task.Run(() => heuristics.ScanAsync(p, ct), ct);
        var protect = new ProtectViewModel(defender, HeuristicScan,
            engine, ProcessTools.KillByPath, dialogs, canChange, gate, actions, securityActions.EnableRealTime, shell);
        // every source runs on the thread pool with its own timeout; the whole check stays off the UI thread
        // the browser's own extensions page opens with the signed-in user's normal rights, never elevated
        var extensionPages = new ExtensionPageOpener(reg, paths, new UserTokenLauncher(user.Sid));
        var securitySources = new SecuritySources(firewall, new WmiSecurityCenter(), defender, reg, new WmiVolumeEncryption(),
            new WuaUpdateSearch(), new TcpTableConnections(), procs, sig, new BrowserExtensionScanner(paths, reg), extensionPages,
            Policies: () => new JournalPolicyOwnership(store), FirewallRules: fwRules, Rules: rules, WindowsDir: windowsDir,
            Actions: securityActions,
            RestoreConfig: restoreConfig, Services: sc);
        Task<IReadOnlyList<SecurityItem>> SecurityRun(CancellationToken ct) =>
            Task.Run(() => new SecurityCheck(securitySources).RunAsync(ct), ct);
        var security = new SecurityViewModel(SecurityRun,
            engine, defender, dialogs, shell, canChange, gate, () => protect.IsDefenderBusy, extensionPages, actions: actions);
        // the DISM probe and the registry reads stay off the UI thread
        Task<IReadOnlyList<PrivacyRow>> PrivacyScan(CancellationToken ct) => Task.Run<IReadOnlyList<PrivacyRow>>(async () =>
        {
            var recall = await RecallProbe.IsPresentAsync(new ProcessRunner(), ct);
            return new PrivacyScanner(reg, rules).Scan(r => r == "recall" && recall);
        }, ct);
        var privacy = new PrivacyViewModel(PrivacyScan, engine, dialogs, canChange, gate);
        // traces (services, tasks, autostarts) for the leftover search and the forced removal
        IReadOnlyList<Finding> TraceScan() => [.. autostart.Scan(), .. services.Scan(), .. tasks.Scan()];
        var programService = new ProgramService(new ProgramInventory(reg, rules), reg, fo, sig, new JobUninstallLauncher(user.Sid),
            new UninstallLeftoverFinder(reg, fo, rules, procs, paths, TraceScan),
            new ForcedRemovalPlanner(reg, fo, rules, procs, paths, TraceScan), paths);
        // Leistung and Programme: the animation switch (SPI) and the app removal act on the signed-in user's account,
        // so they are offered only when the process runs as that user.
        using var identity = WindowsIdentity.GetCurrent();
        var ownAccount = string.Equals(identity.User?.Value, user.Sid, StringComparison.OrdinalIgnoreCase);
        // the view model runs every read of the service (and the app scan) on the thread pool
        IReadOnlyList<BloatAppRow> BloatScan() => new BloatAppScanner(pkgs, rules).Scan();
        var programs = new ProgramsViewModel(programService, engine, dialogs, shell, canChange, gate, BloatScan, ownAccount);
        var perfScanner = new PerformanceScanner(reg, power, animation, hibernation, new WinAppDisplayNames(reg), ownAccount, broadcast,
            plans, () => new JournalPolicyOwnership(store));
        var disk = new WinDiskSpace();
        var perfTools = new PerformanceTools(TraceScan, rules, new WinProcessEnder(procs), PerfGuard, actions,
            disk.SystemDrive, HibernationBytes: () => hibernation.FileBytes);
        var systemSources = new WinSystemSources();
        var bootSource = new WinBootEventSource();
        ShellViewModel? shellVm = null;
        // „Autostart-Programme prüfen“ and „Platz schaffen“: Optimieren with a fresh scan (safe findings preselected)
        void OpenOptimizeAndScan()
        {
            shellVm?.Navigate(optimize);
            if (optimize.ScanCommand.CanExecute(null)) optimize.ScanCommand.Execute(null);
        }
        var performance = new PerformanceViewModel(
            _ => Task.FromResult<(IReadOnlyList<PerfSetting>, Func<PerfSetting, int, IReadOnlyList<IChange>>)>(
                (perfScanner.Scan(), perfScanner.ChangesFor)),
            new SystemSampler(systemSources), () => BootReport.Load(bootSource, DateTime.UtcNow),
            engine, dialogs, shell, canChange, gate, OpenOptimizeAndScan, new DispatcherTickSource(),
            perfScanner.FastStartupActive, perfTools);
        var janitor = new AblageJanitor(store);
        var history = new HistoryViewModel(store, engine, janitor, dialogs, canChange, gate, shell);
        var update = new UpdateViewModel(new UpdateService(env), dialogs, canChange, shutdown, gate);
        var settingsVm = new SettingsViewModel(settings, env, update, shell, rulesInfo, updateRules);

        // „Rechner in Ordnung bringen“: the same scanners as the area pages, in this order (the first source wins a shared change);
        // without admin rights everything is listed and locked (the apps only need the signed-in user's account)
        static ImprovementSource Source(string name, Func<CancellationToken, Task<IReadOnlyList<Improvement>>> read) =>
            new(name, async ct => (await read(ct), []));
        var collector = new ImprovementCollector(
        [
            Source("Optimieren", async ct => ImprovementMap.FromFindings(await engine.ScanAsync(null, ct))),
            Source("Schutz", async ct => ImprovementMap.FromFindings(await HeuristicScan(null, ct))),
            Source("Datenschutz", async ct => ImprovementMap.FromPrivacy(await PrivacyScan(ct))),
            Source("Leistung", ct => Task.Run(() => ImprovementMap.FromPerformance(perfScanner.Scan(), perfScanner.ChangesFor), ct)),
            new("Sicherheit", async ct => ImprovementMap.FromSecurity(await SecurityRun(ct))),
            Source("Vorinstallierte Apps", ct => Task.Run(() => ImprovementMap.FromBloatApps(BloatScan(), ownAccount), ct)),
        ]);
        var improve = new ImproveViewModel((p, ct) => Task.Run(() => collector.CollectAsync(p, ct), ct), engine, dialogs, actions,
            canChange, gate, disk.SystemDrive, shell, OpenOptimizeAndScan);

        shellVm = new ShellViewModel(optimize, performance, protect, security, privacy, programs, history, settingsVm, update,
            improve, env.IsElevated);
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
