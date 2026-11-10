using WinWaechter.Core.Model;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Optimizer.Storage;

/// <summary>Offers old installer files in the Downloads folder (top level only), never preselected.</summary>
public sealed class InstallerScanner
{
    public static readonly string[] Extensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx"];
    public static readonly TimeSpan MinAge = TimeSpan.FromDays(30);

    readonly IFileOps _files;
    readonly ISignatureVerifier _sig;
    readonly string _downloads;

    public InstallerScanner(IFileOps f, ISignatureVerifier sig, string downloadsDir)
    {
        _files = f; _sig = sig; _downloads = downloadsDir;
    }

    public IReadOnlyList<Finding> Scan()
    {
        var result = new List<Finding>();
        if (string.IsNullOrWhiteSpace(_downloads) || !Directory.Exists(_downloads)) return result;
        var cutoff = DateTime.UtcNow - MinAge;
        List<FileInfo> files;
        try { files = _files.EnumerateTopLevel(_downloads).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }

        foreach (var fi in files)
        {
            try
            {
                if (!Extensions.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase)) continue;
                if (fi.LastWriteTimeUtc >= cutoff) continue;
                var info = Verify(fi.FullName);
                result.Add(new Finding($"installer|{fi.FullName}", FindingArea.Speicher, fi.Name, fi.FullName,
                    [$"Zuletzt geändert am {fi.LastWriteTime:dd.MM.yyyy}", SignatureText.Line(info)],
                    Rating.Info, false, MoveToAblageChange.ForFile(_files, fi.FullName), fi.Length, info.Publisher,
                    Group: FindingGroups.OldInstallers));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // vanished or locked meanwhile: skip it
            }
        }
        return result;
    }

    SignatureInfo Verify(string path)
    {
        try { return _sig.Verify(path); }
        catch (Exception) { return new SignatureInfo(SignatureState.Unknown, null, false); }
    }
}
