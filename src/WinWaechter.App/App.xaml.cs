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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
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
        if (s.Settings.CheckUpdatesOnStart) tasks.Add(shell.Update.CheckCommand.ExecuteAsync(null));
        try { await Task.WhenAll(tasks); }
        catch (Exception ex) { FileLog.Warn("Startaufgaben fehlgeschlagen", ex); }
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
