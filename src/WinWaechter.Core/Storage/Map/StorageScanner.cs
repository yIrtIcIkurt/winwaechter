namespace WinWaechter.Core.Storage.Map;

/// <summary>One entry of a folder listing (what the scanner needs; a fake lists these directly).
/// IsLink: a real link (junction, symbolic link); null = decided by the ReparsePoint attribute. OneDrive folders and files
/// carry a cloud reparse point too, but they are no links and are counted.</summary>
public sealed record StorageEntry(string Name, string FullPath, bool IsDir, long Length, FileAttributes Attributes, bool? IsLink = null)
{
    public bool Link => IsLink ?? (Attributes & FileAttributes.ReparsePoint) != 0;
}

/// <summary>A folder of the map. Bytes and Files include all subfolders; OwnFileBytes = files directly in it.</summary>
public sealed class StorageNode(string name, string fullPath, StorageNode? parent)
{
    public string Name { get; } = name;
    public string FullPath { get; } = fullPath;
    public StorageNode? Parent { get; } = parent;
    public List<StorageNode> Children { get; } = [];
    public long Bytes { get; internal set; }
    public long Files { get; internal set; }
    public long OwnFileBytes { get; internal set; }
    public bool NoAccess { get; internal set; }

    /// <summary>Takes this folder out of the tree and subtracts its size from every parent (after a move to the Ablage).</summary>
    public void Detach()
    {
        if (Parent is null) return;
        Parent.Children.Remove(this);
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Bytes -= Bytes;
            p.Files -= Files;
        }
    }
}

public sealed record LargeFile(string FullPath, long Bytes);

public sealed record StorageScan(StorageNode Root, IReadOnlyList<LargeFile> LargestFiles, long Files, long Bytes, int NoAccessCount);

/// <summary>
/// Walks a folder tree iteratively: reparse points (junctions, links) are never followed and count 0 bytes,
/// cloud placeholders (OneDrive „nur online“) count 0 bytes, unreadable folders are marked and skipped.
/// The tree holds folders only; the biggest files are kept in a separate bounded list.
/// </summary>
public sealed class StorageScanner(Func<string, IEnumerable<StorageEntry>>? enumerate = null)
{
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000;

    readonly Func<string, IEnumerable<StorageEntry>> _enumerate = enumerate ?? Real;

    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    static IEnumerable<StorageEntry> Real(string dir)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options))
        {
            StorageEntry? entry;
            try
            {
                var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                // only symbolic links and junctions have a link target; cloud placeholders (OneDrive) do not
                var isLink = (e.Attributes & FileAttributes.ReparsePoint) != 0 && IsRealLink(e);
                entry = new StorageEntry(e.Name, e.FullName, isDir, isDir ? 0 : ((FileInfo)e).Length, e.Attributes, isLink);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry = null;   // vanished or unreadable meanwhile
            }
            if (entry is not null) yield return entry;
        }
    }

    static bool IsRealLink(FileSystemInfo e)
    {
        try { return e.LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }   // unclear: never follow
    }

    public Task<StorageScan> ScanAsync(string root, IProgress<(long Files, long Bytes)>? progress, CancellationToken ct,
        int maxLarge = 100, long minLarge = 100L << 20) => Task.Run(() => Scan(root, progress, ct, maxLarge, minLarge), ct);

    StorageScan Scan(string root, IProgress<(long Files, long Bytes)>? progress, CancellationToken ct, int maxLarge, long minLarge)
    {
        ct.ThrowIfCancellationRequested();
        var top = new StorageNode(Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, root, null);
        var order = new List<StorageNode>();
        var stack = new Stack<StorageNode>();
        stack.Push(top);
        var large = new PriorityQueue<LargeFile, long>();
        long files = 0, bytes = 0;
        var noAccess = 0;
        var lastReport = Environment.TickCount64;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = stack.Pop();
            order.Add(node);
            try
            {
                foreach (var e in _enumerate(node.FullPath))
                {
                    if (e.IsDir)
                    {
                        var child = new StorageNode(e.Name, e.FullPath, node);
                        node.Children.Add(child);
                        // a junction or link is listed, but never entered (no double counting, no cycles)
                        if (!e.Link) stack.Push(child);
                        continue;
                    }
                    files++;
                    node.Files++;
                    // „nur online“ and links take no local space; locally present OneDrive files do
                    if ((e.Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0 || e.Link) continue;
                    node.OwnFileBytes += e.Length;
                    bytes += e.Length;
                    if (e.Length >= minLarge && maxLarge > 0)
                    {
                        large.Enqueue(new LargeFile(e.FullPath, e.Length), e.Length);
                        if (large.Count > maxLarge) large.Dequeue();
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                node.NoAccess = true;
                noAccess++;
            }
            if (progress is not null && Environment.TickCount64 - lastReport >= 250)
            {
                lastReport = Environment.TickCount64;
                progress.Report((files, bytes));
            }
        }

        // post-order roll-up: children were visited after their parents
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            node.Bytes += node.OwnFileBytes;
            if (node.Parent is { } p)
            {
                p.Bytes += node.Bytes;
                p.Files += node.Files;
            }
        }
        progress?.Report((files, bytes));

        var largest = new List<LargeFile>();
        while (large.Count > 0) largest.Add(large.Dequeue());
        largest.Reverse();
        return new StorageScan(top, largest, files, bytes, noAccess);
    }
}
