using System.Text.RegularExpressions;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Privacy;

/// <summary>Checks whether the Windows Recall optional feature exists on this PC (read-only DISM query).</summary>
public static class RecallProbe
{
    public static async Task<bool> IsPresentAsync(IProcessRunner runner, CancellationToken ct)
    {
        try
        {
            var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
            var (exit, stdout, _) = await runner.RunAsync(dism, "/English /Online /Get-FeatureInfo /FeatureName:Recall", ct);
            return exit == 0 && Regex.IsMatch(stdout ?? "", @"Feature Name\s*:\s*Recall", RegexOptions.IgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
