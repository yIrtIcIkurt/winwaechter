using WinWaechter.Core.Programs;

namespace WinWaechter.Core.Storage.Map;

/// <summary>What kind of place a path is; decides which offers a folder or file gets.</summary>
public enum PlaceKind
{
    Normal, Temp, BrowserCache, WindowsUpdateDownload, RecycleBin, Hibernation, PageFile, WinSxS, Windows, ProgramFiles, ProgramData,
    OtherUserProfile, OneDrive, UserLibrary, ProgramFolder, Protected,
}

/// <summary>The places of this machine. ProgramFor: the installed program whose folder contains the path.</summary>
public sealed record PlaceContext(string WindowsDir, string ProgramFiles, string ProgramFilesX86, string ProgramData, string UserProfile,
    string TempDir, IReadOnlyList<string> OneDriveRoots, IReadOnlyList<string> UserLibraries, Func<string, InstalledProgram?> ProgramFor,
    Func<string, bool> IsProtectedPath);

public static class PlaceKinds
{
    // the cache folder names the browsers use (same list as the cache cleanup)
    static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "cache2", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
    };

    static bool Under(string path, string root) =>
        root.Length > 0 && (path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first matching kind in a fixed order: protection and OneDrive before everything else.</summary>
    public static PlaceKind Classify(string path, PlaceContext c)
    {
        var p = path.TrimEnd('\\');
        var rootDir = Path.GetPathRoot(p)?.TrimEnd('\\') ?? "";
        var rest = p.Length > rootDir.Length ? p[(rootDir.Length + 1)..] : "";

        if (c.IsProtectedPath(p + "\\")) return PlaceKind.Protected;
        if (c.OneDriveRoots.Any(r => Under(p, r))) return PlaceKind.OneDrive;
        // Windows keeps it at each volume root; a scan of a sub-folder can still meet one there
        if (p.Split('\\').Any(s => s.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))) return PlaceKind.RecycleBin;
        if (rest.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)) return PlaceKind.Hibernation;
        if (rest.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || rest.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            return PlaceKind.PageFile;
        if (Under(p, Path.Combine(c.WindowsDir, "WinSxS"))) return PlaceKind.WinSxS;
        if (Under(p, Path.Combine(c.WindowsDir, "SoftwareDistribution", "Download"))) return PlaceKind.WindowsUpdateDownload;
        if (Under(p, c.TempDir) || Under(p, Path.Combine(c.WindowsDir, "Temp"))) return PlaceKind.Temp;
        if (Under(p, Path.Combine(c.UserProfile, "AppData")) && p.Split('\\').Any(CacheNames.Contains)) return PlaceKind.BrowserCache;
        if (Under(p, c.WindowsDir)) return PlaceKind.Windows;
        if (c.ProgramFor(p) is not null) return PlaceKind.ProgramFolder;
        if (Under(p, c.ProgramFiles) || Under(p, c.ProgramFilesX86)) return PlaceKind.ProgramFiles;
        if (Under(p, c.ProgramData)) return PlaceKind.ProgramData;
        var users = Path.GetDirectoryName(c.UserProfile.TrimEnd('\\')) ?? "";
        if (Under(p, users) && !Under(p, c.UserProfile)) return PlaceKind.OtherUserProfile;
        // only what lies below a library: the Downloads (Videos …) folder itself must stay where Windows expects it
        if (c.UserLibraries.Any(l => Under(p, l) && !p.Equals(l.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return PlaceKind.UserLibrary;
        return PlaceKind.Normal;
    }

    /// <summary>True when the folder or anything scanned below it is protected (e.g. Lexware) or in OneDrive:
    /// moving the folder would move those too.</summary>
    public static bool ContainsBlocked(StorageNode node, PlaceContext c)
    {
        var stack = new Stack<StorageNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (Classify(n.FullPath, c) is PlaceKind.Protected or PlaceKind.OneDrive) return true;
            foreach (var child in n.Children) stack.Push(child);
        }
        return false;
    }
}

public enum StorageOfferKind { ShowInExplorer, Cleanup, EmptyRecycleBin, OpenHibernation, Explain, Uninstall, MoveToAblage }

/// <summary>One thing WinWächter offers for a folder or file. Group: the Optimieren group to preselect (Cleanup).</summary>
public sealed record StorageOffer(string Text, StorageOfferKind Kind, string? Group = null, string? Note = null);

public static class StorageOffers
{
    public const string TempGroup = "temp";

    public static IReadOnlyList<StorageOffer> For(PlaceKind k, InstalledProgram? program, int ablageDays)
    {
        var list = new List<StorageOffer>();
        switch (k)
        {
            case PlaceKind.Temp:
                list.Add(new("Aufräumen", StorageOfferKind.Cleanup, TempGroup));
                break;
            case PlaceKind.BrowserCache:
                list.Add(new("Aufräumen", StorageOfferKind.Cleanup, Model.FindingGroups.BrowserCache));
                break;
            case PlaceKind.WindowsUpdateDownload:
                list.Add(new("Aufräumen", StorageOfferKind.Cleanup, Model.FindingGroups.WindowsRemnants));
                break;
            case PlaceKind.RecycleBin:
                list.Add(new("Papierkorb leeren", StorageOfferKind.EmptyRecycleBin));
                break;
            case PlaceKind.Hibernation:
                list.Add(new("Ruhezustand ansehen", StorageOfferKind.OpenHibernation));
                break;
            case PlaceKind.PageFile or PlaceKind.WinSxS:
                list.Add(new("Von Windows verwaltet, bitte nicht anfassen.", StorageOfferKind.Explain));
                break;
            case PlaceKind.ProgramFolder when program is { Flag: not ProgramFlag.Protected }:
                list.Add(new("Programm deinstallieren", StorageOfferKind.Uninstall));
                break;
            case PlaceKind.UserLibrary:
                list.Add(new("In die Ablage verschieben", StorageOfferKind.MoveToAblage,
                    Note: $"Der Platz wird frei, wenn die Ablage geleert wird (nach {ablageDays} Tagen)."));
                break;
        }
        list.Add(new("Im Explorer zeigen", StorageOfferKind.ShowInExplorer));
        return list;
    }
}
