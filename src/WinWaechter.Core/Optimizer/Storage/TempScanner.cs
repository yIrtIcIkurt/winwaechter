using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>Offers old files in the user temp folder and C:\Windows\Temp for the Ablage.</summary>
public sealed class TempScanner
{
    readonly IFileOps _files;
    readonly IUserContext _user;
    readonly string _systemTemp;

    public TempScanner(IFileOps f, IUserContext u, string? systemTempDir = null)
    {
        _files = f; _user = u;
        _systemTemp = systemTempDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        Add(result, _user.TempDir, "Temporäre Dateien (Benutzer)");
        if (!string.Equals(Path.GetFullPath(_user.TempDir), Path.GetFullPath(_systemTemp), StringComparison.OrdinalIgnoreCase))
            Add(result, _systemTemp, "Temporäre Dateien (Windows)");
        return result;
    }

    void Add(List<Finding> result, string folder, string title)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
        var minAge = MoveToAblageChange.DefaultMinAge;
        var (files, _) = MoveToAblageChange.Collect(_files, folder, minAge);
        if (files.Count == 0) return;
        var size = files.Sum(f => f.Length);
        var days = (int)minAge.TotalDays;
        result.Add(new Finding($"storage|{folder}", FindingArea.Speicher, title, folder,
            [$"{files.Count} Dateien älter als {days} Tage"],
            Rating.Entbehrlich, true, new MoveToAblageChange(_files, folder, minAge), size));
    }
}
