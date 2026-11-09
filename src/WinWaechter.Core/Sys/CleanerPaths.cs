using Microsoft.Win32;

namespace WinWaechter.Core.Sys;

public sealed record CleanerPaths(string LocalAppData, string RoamingAppData, string SystemRoot, string ProgramData,
    string ProgramFiles, string ProgramFilesX86)
{
    // AppData follows the interactive user, not the (possibly different) elevated process user.
    public static CleanerPaths For(IUserContext u) => new(
        Path.Combine(u.ProfileDir, "AppData", "Local"),
        Path.Combine(u.ProfileDir, "AppData", "Roaming"),
        Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
        Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
        Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)");
}

public static class DownloadsFolder
{
    public const string KnownFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";
    const string Base = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";

    public static string Resolve(IRegistry reg, IUserContext u)
    {
        foreach (var key in new[] { Base + "User Shell Folders", Base + "Shell Folders" })
        {
            if (reg.Get(Hive.CurrentUser, key, KnownFolderId) is string s && !string.IsNullOrWhiteSpace(s))
            {
                var withProfile = s.Replace("%USERPROFILE%", u.ProfileDir, StringComparison.OrdinalIgnoreCase);
                return Environment.ExpandEnvironmentVariables(withProfile);
            }
        }
        return Path.Combine(u.ProfileDir, "Downloads");
    }
}
