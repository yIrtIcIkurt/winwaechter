namespace WinWaechter.Core.Sys;

// The interactive (logged-on) user, which may differ from the elevated process user.
public interface IUserContext
{
    string Sid { get; }
    string ProfileDir { get; }
    string TempDir { get; }
    string StartupDir { get; }
    string CommonStartupDir { get; }
}
