using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using WinWaechter.Core.Maintenance;

namespace WinWaechter.Notify;

/// <summary>
/// WinWaechter.Notify.exe --title "…" --text "…": shows one Windows notification under WinWächter's
/// AppUserModelID and exits. No window, no dialog; exit code 1 when it could not be shown.
/// </summary>
internal static class Program
{
    static int Main(string[] args)
    {
        string? title = null, text = null;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--title") title = args[++i];
            else if (args[i] == "--text") text = args[++i];
        }
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(text)) return 1;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(ToastXml.Build(title, text));
            ToastNotificationManager.CreateToastNotifier(ToastXml.AppId).Show(new ToastNotification(doc));
            // give the notification platform a moment before the process goes away
            Thread.Sleep(TimeSpan.FromSeconds(3));
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
