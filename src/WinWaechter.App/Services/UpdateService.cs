using System.IO;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Update;

namespace WinWaechter.App.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the release and verifies it. The returned object holds the setup open.</summary>
    Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct);

    void Launch(VerifiedUpdate update);
}

/// <summary>GitHub update flow from Core: check, download, verify, launch.</summary>
public sealed class UpdateService(AppEnvironment env) : IUpdateService
{
    public Task<UpdateCheckResult> CheckAsync(CancellationToken ct) =>
        new UpdateChecker(UpdateHttp.CreateHandler(), env.Version).CheckAsync(ct);

    public async Task<VerifiedUpdate> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var target = await Task.Run(() => SecureDataDir.Ensure(env.UpdatesDir), ct);
        var (sums, sig, setup) = await new UpdateDownloader(UpdateHttp.CreateHandler())
            .DownloadAsync(info, target, progress, ct);
        try
        {
            return await Task.Run(() => new ReleaseVerifier(UpdateKeys.PublicKeyPem, env.Version).Verify(sums, sig, setup, info), ct);
        }
        catch
        {
            // a rejected download is removed right away
            try { Directory.Delete(Path.GetDirectoryName(setup)!, true); } catch { /* best effort */ }
            throw;
        }
    }

    public void Launch(VerifiedUpdate update) => UpdateInstaller.Launch(update);
}
