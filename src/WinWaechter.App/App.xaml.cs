using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using WinWaechter.App.Services;
using WinWaechter.App.Views;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.App;

/// <summary>Startup: single instance, data folder, logging, settings, theme, window.</summary>
public partial class App
{
    const string MutexName = @"Local\WinWaechter.SingleInstance";
    Mutex? _instance;
    bool _ownsMutex;
    AppServices? _services;

    const string CheckMutexName = @"Local\WinWaechter.Check";
    IDisposable? _activation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mode = StartupArgs.Parse(e.Args);
        if (mode == StartupMode.Check)
        {
            // the weekly check: no window, its own single-instance lock, never the app's
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunCheckAsync();
            return;
        }

        _instance = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // „Ansehen“ on the notification while WinWächter is open: the running window takes over
            if (mode == StartupMode.Improve)
                ActivationPipe.Send(ActivationPipe.NameFor(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value), ActivationPipe.Verb);
            BringExistingToFront();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => FileLog.Error("Unbehandelter Fehler", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            FileLog.Error("Unbeobachteter Fehler in einer Aufgabe", a.Exception);
            a.SetObserved();
        };

        var env = AppEnvironment.Detect();
        try
        {
            if (env.IsElevated) SecureDataDir.Ensure(env.DataRoot);
            else Directory.CreateDirectory(env.DataRoot);
        }
        catch (Exception ex)
        {
            ShowError("Der Datenordner konnte nicht angelegt werden: " + env.DataRoot, ex);
            Shutdown(1);
            return;
        }
        FileLog.Init(env.LogDir);
        FileLog.Info($"WinWächter {env.Version} startet ({(env.IsElevated ? "Administrator" : "ohne Adminrechte")}), Daten: {env.DataRoot}");

        try
        {
            var settings = AppSettings.Load(env.SettingsPath);
            ThemeManager.Start(this);
            WheelScrolling.Register();

            _services = AppServices.Create(env, settings, new WpfDialogService(), new ExplorerShell(), () => Shutdown());
            var window = new ShellWindow { DataContext = _services.Shell };
            MainWindow = window;
            window.Show();
            var services = _services;
            _activation = ActivationPipe.Listen(ActivationPipe.NameFor(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value),
                _ => Dispatcher.BeginInvoke(() => ShowImprove(services)));
            if (mode == StartupMode.Improve) ShowImprove(services);
        }
        catch (Exception ex)
        {
            // without a window the app would keep running invisibly
            ShowError("WinWächter konnte nicht starten.", ex);
            Shutdown(1);
            return;
        }

        _ = StartBackgroundWorkAsync(_services);
    }

    // Purge old Ablage files and update downloads, read Defender state and the history, check for
    // updates; never blocks the window.
    static async Task StartBackgroundWorkAsync(AppServices s)
    {
        try
        {
            var days = s.Settings.AblageDays;
            // through the change gate, so no undo or apply runs on the same sessions meanwhile
            var ran = await s.Gate.RunAsync(s.Janitor, async () =>
            {
                var freed = await Task.Run(() => s.Janitor.Purge(TimeSpan.FromDays(days)));
                if (freed > 0) FileLog.Info($"Ablage: {freed} Byte älter als {days} Tage gelöscht");
            });
            if (!ran) FileLog.Info("Ablage-Bereinigung beim Start übersprungen, eine andere Änderung lief");
        }
        catch (Exception ex) { FileLog.Warn("Ablage-Bereinigung beim Start fehlgeschlagen", ex); }

        try
        {
            var removed = await Task.Run(() => UpdateCleanup.PurgeOld(s.Env.UpdatesDir, UpdateCleanup.DefaultMaxAge));
            if (removed > 0) FileLog.Info($"Updates: {removed} alte Download-Ordner gelöscht");
        }
        catch (Exception ex) { FileLog.Warn("Bereinigung alter Update-Downloads fehlgeschlagen", ex); }

        var shell = s.Shell;
        var tasks = new List<Task>
        {
            shell.Protect.RefreshCommand.ExecuteAsync(null),
            shell.Security.RefreshCommand.ExecuteAsync(null),
            shell.History.RefreshCommand.ExecuteAsync(null),
        };
        if (s.Settings.CheckUpdatesOnStart)
        {
            tasks.Add(shell.Update.CheckCommand.ExecuteAsync(null));
            tasks.Add(shell.Settings.UpdateRulesCommand.ExecuteAsync(null));
        }
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) { FileLog.Warn("Startaufgaben fehlgeschlagen", ex); }
    }

    // „Rechner in Ordnung bringen“: the overview in front, the check started
    void ShowImprove(AppServices s)
    {
        if (MainWindow is { } w)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
        s.Shell.Navigate(s.Shell.Overview);
        if (s.Shell.Overview.Improve.CheckCommand.CanExecute(null)) s.Shell.Overview.Improve.CheckCommand.Execute(null);
    }

    // --pruefen: read everything, save the result, notify through the unelevated helper; changes nothing
    async Task RunCheckAsync()
    {
        using var mutex = new Mutex(true, CheckMutexName, out var owns);
        if (!owns)
        {
            Shutdown(0);
            return;
        }
        var code = 0;
        try
        {
            var env = AppEnvironment.Detect();
            if (env.IsElevated) SecureDataDir.Ensure(env.DataRoot);
            else Directory.CreateDirectory(env.DataRoot);
            FileLog.Init(env.LogDir);
            FileLog.Info($"WinWächter {env.Version}: wöchentliche Prüfung startet");
            var settings = AppSettings.Load(env.SettingsPath);
            var services = AppServices.Create(env, settings, new SilentDialogs(), new ExplorerShell(), () => { });
            var notifyExe = Path.Combine(AppContext.BaseDirectory, "WinWaechter.Notify.exe");
            var launcher = new Core.Programs.UserTokenLauncher(services.UserSid);
            var check = new Core.Maintenance.MaintenanceCheck(services.Collector, services.UpdateCount, services.AppUpdateAvailable, env.DataRoot,
                (title, text) => launcher.StartAsUser(notifyExe,
                    $"--title {Core.Maintenance.CommandLineArgs.Quote(title)} --text {Core.Maintenance.CommandLineArgs.Quote(text)}"),
                () => DateTimeOffset.Now);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(25));
            await check.RunAsync(timeout.Token);
            services.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Error("Wöchentliche Prüfung abgebrochen", ex);
            code = 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        Shutdown(code);
    }

    /// <summary>The weekly check never shows a dialog: questions are answered „no“, messages only logged.</summary>
    sealed class SilentDialogs : IDialogService
    {
        public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) => false;
        public bool Ask(string title, string message, string yesText, string noText) => false;
        public void Inform(string title, string message, IReadOnlyList<string>? items = null) => FileLog.Info($"{title}: {message}");
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowError("Ein unerwarteter Fehler ist aufgetreten. Die letzte Aktion wurde nicht abgeschlossen.", e.Exception);
    }

    // WINWAECHTER_NO_DIALOGS=1 (automated smoke runs): log only, never block the desktop with a dialog
    static readonly bool NoDialogs = Environment.GetEnvironmentVariable("WINWAECHTER_NO_DIALOGS") == "1";

    static void ShowError(string message, Exception ex)
    {
        FileLog.Error(message, ex);
        if (NoDialogs) return;
        try
        {
            MessageBox.Show($"{message}\n\n{ex.Message}\n\nDetails stehen im Protokoll.", "WinWächter – Fehler",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* nothing left to show it with */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Info("WinWächter wird beendet");
        _activation?.Dispose();
        _services?.Dispose();
        if (_instance is not null)
        {
            if (_ownsMutex)
            {
                try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    // ---- second start: show the running window ----

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    const int SwRestore = 9;

    static void BringExistingToFront()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SwRestore);
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception) { /* best effort only */ }
    }
}
