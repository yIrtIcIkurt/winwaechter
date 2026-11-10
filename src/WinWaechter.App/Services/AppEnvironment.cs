using System.IO;
using System.Reflection;
using System.Security.Principal;
using WinWaechter.Core.Update;

namespace WinWaechter.App.Services;

/// <summary>Where the app keeps its data and whether it runs with admin rights.</summary>
public sealed record AppEnvironment(bool IsElevated, string DataRoot, SemVer Version)
{
    public string LogDir => Path.Combine(DataRoot, "Logs");
    public string UpdatesDir => Path.Combine(DataRoot, "Updates");
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string RulesDir => Path.Combine(DataRoot, "Regeln");

    public const string AdminNotice =
        "Für diese Aktion sind Administratorrechte nötig. Bitte WinWächter als Administrator starten.";

    public static AppEnvironment Detect()
    {
        bool elevated;
        using (var id = WindowsIdentity.GetCurrent())
            elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);

        // Without admin rights (Debug) the protected ProgramData folder cannot be secured, so use a dev folder.
        var root = elevated
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinWaechter")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinWaechter-Dev");

        var v = typeof(AppEnvironment).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new AppEnvironment(elevated, root, new SemVer(v.Major, v.Minor, Math.Max(0, v.Build)));
    }
}
