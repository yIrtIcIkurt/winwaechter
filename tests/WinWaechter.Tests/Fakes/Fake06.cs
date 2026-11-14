using WinWaechter.Core.Programs;
using WinWaechter.Core.Updates;

namespace WinWaechter.Tests.Fakes;

// List result is configurable; UpgradeHandler decides the exit code per package (default 0).
public sealed class FakeWinget : IWingetClient
{
    public WingetList List { get; set; } = new(WingetListStatus.Ok, [], null);
    public List<bool> ListCalls { get; } = [];
    public List<WingetPackage> Upgraded { get; } = [];
    public Func<WingetPackage, int>? UpgradeHandler { get; set; }

    public Task<WingetList> ListUpgradesAsync(bool acceptSourceAgreements, CancellationToken ct)
    {
        ListCalls.Add(acceptSourceAgreements);
        return Task.FromResult(List);
    }

    public Task<int> UpgradeAsync(WingetPackage p, Action<string>? onLine, CancellationToken ct)
    {
        Upgraded.Add(p);
        return Task.FromResult(UpgradeHandler?.Invoke(p) ?? 0);
    }
}

// In-memory folders and files for the repair tools; Locked files throw on delete.
public sealed class FakeRepairFiles : WinWaechter.Core.Repair.IRepairFiles
{
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FileSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];
    public Exception? MoveThrows { get; set; }

    static string Parent(string p) => Path.GetDirectoryName(p) ?? "";

    public bool DirectoryExists(string dir) => Dirs.Contains(dir);
    public IEnumerable<string> Directories(string dir) => Dirs.Where(d => string.Equals(Parent(d), dir, StringComparison.OrdinalIgnoreCase)).ToList();

    public IEnumerable<string> Files(string dir, string pattern)
    {
        var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return FileSet.Where(f => string.Equals(Parent(f), dir, StringComparison.OrdinalIgnoreCase) && regex.IsMatch(Path.GetFileName(f))).ToList();
    }

    public void DeleteFile(string path)
    {
        if (Locked.Contains(path)) throw new IOException("gesperrt");
        FileSet.Remove(path);
        Log.Add("del " + path);
    }

    public void DeleteDirectory(string dir)
    {
        Dirs.RemoveWhere(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase) || d.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));
        Log.Add("rmdir " + dir);
    }

    public void MoveDirectory(string src, string dst)
    {
        if (MoveThrows is not null) throw MoveThrows;
        if (!Dirs.Remove(src)) throw new DirectoryNotFoundException(src);
        Dirs.Add(dst);
        Log.Add($"move {src} -> {dst}");
    }
}

public sealed class FakeNetworkConfig(params WinWaechter.Core.Repair.AdapterConfig[] adapters) : WinWaechter.Core.Repair.INetworkConfig
{
    public IReadOnlyList<WinWaechter.Core.Repair.AdapterConfig> Read() => adapters;
}

// Records user-context runs; Handler decides the exit code.
public sealed class FakeUserProcessRunner : IUserProcessRunner
{
    public List<(string Exe, string Args)> Calls { get; } = [];
    public Func<string, string, int>? Handler { get; set; }

    public Task<int> RunAsUserAsync(string exe, string args, CancellationToken ct)
    {
        lock (Calls) Calls.Add((exe, args));
        return Task.FromResult(Handler?.Invoke(exe, args) ?? 0);
    }
}
