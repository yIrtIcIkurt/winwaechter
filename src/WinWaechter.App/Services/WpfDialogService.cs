using System.IO;
using System.Diagnostics;
using System.Windows;
using WinWaechter.App.Views;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.Services;

/// <summary>Shows the themed dialog window owned by the active window.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false) =>
        Show(new MessageDialog(title, message, items, confirmText, final));

    public bool Ask(string title, string message, string yesText, string noText) =>
        Show(new MessageDialog(title, message, null, yesText, false, noText));

    public void Inform(string title, string message, IReadOnlyList<string>? items = null) =>
        Show(new MessageDialog(title, message, items, null, false));

    static bool Show(MessageDialog d)
    {
        var app = Application.Current;
        var owner = app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app?.MainWindow;
        if (owner is not null && owner.IsVisible) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true;
    }
}

/// <summary>Opens folders in Explorer and allowed system links.</summary>
public sealed class ExplorerShell : ISystemShell
{
    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Ordner konnte nicht geöffnet werden: {path}", ex);
        }
    }

    public void OpenLink(string uri)
    {
        // only the fixed list goes to the shell, never a URI from data
        if (!SystemLinks.IsAllowed(uri))
        {
            FileLog.Warn($"Link nicht erlaubt: {uri}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Link konnte nicht geöffnet werden: {uri}", ex);
        }
    }
}
