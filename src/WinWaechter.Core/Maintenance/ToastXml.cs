using System.Security;

namespace WinWaechter.Core.Maintenance;

/// <summary>
/// The notification XML: title, text, „Ansehen“; a click opens winwaechter:verbessern (protocol activation).
/// Only base-library code: the notification helper compiles this file directly.
/// </summary>
public static class ToastXml
{
    public const string Launch = "winwaechter:verbessern";

    /// <summary>AppUserModelID of WinWächter (the Start menu shortcut carries the same).</summary>
    public const string AppId = "TahsinYilmaz.WinWaechter";

    public static string Build(string title, string text)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        return $"<toast launch=\"{Launch}\" activationType=\"protocol\"><visual><binding template=\"ToastGeneric\">"
               + $"<text>{E(title)}</text><text>{E(text)}</text></binding></visual>"
               + $"<actions><action content=\"Ansehen\" arguments=\"{Launch}\" activationType=\"protocol\" /></actions></toast>";
    }
}
