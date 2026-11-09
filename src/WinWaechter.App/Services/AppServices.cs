using WinWaechter.App.ViewModels;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Optimizer.Autostart;
using WinWaechter.Core.Optimizer.Services;
using WinWaechter.Core.Optimizer.Storage;
using WinWaechter.Core.Optimizer.Tasks;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Security.Heuristics;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.Services;

/// <summary>Composition root: wires Core services and view models with plain constructors.</summary>
public sealed class AppServices
{
    AppServices(AppEnvironment env, AppSettings settings, AblageJanitor janitor, ChangeGate gate, ShellViewModel shell)
    {
        Env = env;
        Settings = settings;
        Janitor = janitor;
        Gate = gate;
        Shell = shell;
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
        var rules = RuleSet.Default;

        // elevated: only journals/sidecars owned by Administrators or SYSTEM may steer an undo
        var store = new SessionStore(env.DataRoot,
            env.IsElevated ? FileOwner.IsAdminOrSystem : null,
            env.IsElevated ? FileOwner.SetAdmins : null);
        var defender = new DefenderClient(new ProcessRunner(), reg);
        var factory = ChangeFactory.CreateDefault(reg, sc, ts, fo, rules);
        DefenderRegistration.Register(factory, defender);

        var autostart = new AutostartScanner(reg, user, sig, rules, fo, lnk);
        var services = new ServiceScanner(sc, sig, rules, reg, fo);
        var tasks = new TaskScanner(ts, sig, rules, fo);
        var temp = new TempScanner(fo, user);
        var paths = CleanerPaths.For(user);
        var procs = new WinProcessList();
        var cache = new CacheScanner(fo, paths, procs, sc, reg);
        var leftovers = new LeftoverFolderScanner(fo, reg, rules, procs, sc, ts, LeftoverFolderScanner.DefaultRoots(paths));
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
        var janitor = new AblageJanitor(store);
        var history = new HistoryViewModel(store, engine, janitor, dialogs, canChange, gate);
        var update = new UpdateViewModel(new UpdateService(env), dialogs, canChange, shutdown, gate);
        var settingsVm = new SettingsViewModel(settings, env, update, shell);

        return new AppServices(env, settings, janitor, gate,
            new ShellViewModel(optimize, protect, history, settingsVm, update, env.IsElevated));
    }
}
