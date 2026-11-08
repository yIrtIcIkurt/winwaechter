using System.Diagnostics;

namespace WinWaechter.Core.Update;

public static class UpdateInstaller
{
    internal const string SetupArguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

    // Call right after verification; the caller shuts the app down afterwards.
    public static void Launch(VerifiedUpdate u)
    {
        var psi = BuildStartInfo(u);
        Process.Start(psi)?.Dispose();
    }

    internal static ProcessStartInfo BuildStartInfo(VerifiedUpdate u)
    {
        if (!Path.IsPathFullyQualified(u.SetupPath))
            throw new ArgumentException("Der Pfad zur Setup-Datei muss absolut sein.", nameof(u));
        return new ProcessStartInfo(u.SetupPath)
        {
            Arguments = SetupArguments,
            UseShellExecute = false,
        };
    }
}
