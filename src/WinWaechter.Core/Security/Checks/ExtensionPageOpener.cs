using WinWaechter.Core.Logging;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Security.Checks;

/// <summary>
/// Leads the user to a browser's own page for one extension, where they remove it with one click.
/// Chromium browsers ignore internal pages (edge://…, chrome://…) handed over by other programs and show a new tab
/// instead, so the browser is opened in a new window and the user pastes <see cref="Address"/> there.
/// </summary>
public interface IExtensionPages
{
    /// <summary>"&lt;scheme&gt;://extensions/?id=&lt;id&gt;". Throws ExtensionPageException with a German reason that is safe to show.</summary>
    string Address(string browser, string id);

    /// <summary>Starts the browser in a new window. Throws ExtensionPageException with a German reason that is safe to show.</summary>
    void Open(string browser);
}

public sealed class ExtensionPageException(string message) : InvalidOperationException(message);

/// <summary>
/// Starts the browser itself (never ShellExecute of a browser URL) with exactly "--new-window", with the signed-in
/// user's normal rights. The browser comes from App Paths (HKLM, then the user's hive) and is used only when it is an
/// existing .exe under Program Files, Program Files (x86) or the user's %LOCALAPPDATA%\Programs; the id in the address
/// must be a Chromium id (32 letters a–p).
/// </summary>
public sealed class ExtensionPageOpener(IRegistry reg, CleanerPaths paths, IUserLauncher launcher) : IExtensionPages
{
    const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    static readonly Dictionary<string, (string Exe, string Scheme)> Browsers = new(StringComparer.Ordinal)
    {
        ["Edge"] = ("msedge.exe", "edge"),
        ["Chrome"] = ("chrome.exe", "chrome"),
        ["Opera"] = ("opera.exe", "opera"),
        ["Brave"] = ("brave.exe", "brave"),
    };

    public bool CanOpen(string browser) => BrowserExe(browser) is not null;

    public string Address(string browser, string id)
    {
        var b = Known(browser);
        if (!BrowserExtensionScanner.IsChromiumId(id))
            throw new ExtensionPageException("Die Kennung der Erweiterung ist ungültig.");
        return $"{b.Scheme}://extensions/?id={id}";
    }

    public void Open(string browser)
    {
        Known(browser);
        var exe = BrowserExe(browser)
            ?? throw new ExtensionPageException($"{browser} wurde nicht in einem Programmordner gefunden.");
        try
        {
            launcher.StartAsUser(exe, "--new-window");
        }
        catch (Exception ex)
        {
            FileLog.Warn($"{browser} ließ sich nicht starten: {exe}", ex);
            var detail = ex is UserLaunchException { Error: not 0 } u
                ? $" (Fehler {u.Error}: {new System.ComponentModel.Win32Exception(u.Error).Message})" : "";
            throw new ExtensionPageException($"{browser} ließ sich nicht mit Ihren normalen Benutzerrechten starten{detail}. " +
                                             "Öffnen Sie im Browser die Seite „Erweiterungen“ und entfernen Sie sie dort.");
        }
    }

    static (string Exe, string Scheme) Known(string browser) => Browsers.TryGetValue(browser, out var b) ? b
        : throw new ExtensionPageException($"Für {browser} kann WinWächter die Seite der Erweiterung nicht öffnen.");

    /// <summary>The browser's exe from App Paths when it is trusted, else null.</summary>
    public string? BrowserExe(string browser)
    {
        if (!Browsers.TryGetValue(browser, out var b)) return null;
        string[] roots = [paths.ProgramFiles, paths.ProgramFilesX86, Path.Combine(paths.LocalAppData, "Programs")];
        // the user's hive only for Opera, which installs per user and registers itself only there
        Hive[] hives = browser == "Opera" ? [Hive.LocalMachine, Hive.CurrentUser] : [Hive.LocalMachine];
        foreach (var hive in hives)
        {
            try
            {
                if (Trusted(reg.Get(hive, $@"{AppPaths}\{b.Exe}", "") as string, roots, b.Exe) is { } exe) return exe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                FileLog.Warn($"App Paths für {b.Exe} nicht lesbar", ex);
            }
        }
        return null;
    }

    // Fully qualified local path (no \\server, no %VAR%), exactly the expected file name (e.g. msedge.exe), existing,
    // no link, below one of the roots.
    internal static string? Trusted(string? value, IReadOnlyList<string> roots, string fileName)
    {
        var raw = value?.Trim();
        if (raw is { Length: > 2 } && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1];
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOfAny(['"', '%']) >= 0) return null;
        if (raw.Length < 3 || !char.IsAsciiLetter(raw[0]) || raw[1] != ':' || raw[2] != '\\') return null;
        try
        {
            var full = Path.GetFullPath(raw);
            if (!string.Equals(Path.GetFileName(full), fileName, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) return null;
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (r.Length > 3 && full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return full;
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
