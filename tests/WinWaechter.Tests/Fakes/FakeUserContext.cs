using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Fixed user; directories default to subfolders of a given root (e.g. a test temp dir).
public sealed class FakeUserContext : IUserContext
{
    public FakeUserContext(string root, string sid = "S-1-5-21-1000-1000-1000-1001")
    {
        Sid = sid;
        ProfileDir = root;
        TempDir = Path.Combine(root, "AppData", "Local", "Temp");
        StartupDir = Path.Combine(root, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        CommonStartupDir = Path.Combine(root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "StartUp");
    }

    public string Sid { get; set; }
    public string ProfileDir { get; set; }
    public string TempDir { get; set; }
    public string StartupDir { get; set; }
    public string CommonStartupDir { get; set; }
}
