using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Update;

namespace WinWaechter.App.ViewModels;

/// <summary>Update state shared by the banner and the settings page: check → download + verify → install.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    readonly IUpdateService _service;
    readonly IDialogService _dialogs;
    readonly Action _shutdown;
    VerifiedUpdate? _verified;

    public UpdateViewModel(IUpdateService service, IDialogService dialogs, bool canInstall, Action shutdown)
    {
        _service = service;
        _dialogs = dialogs;
        CanInstall = canInstall;
        _shutdown = shutdown;
    }

    public bool CanInstall { get; }
    public bool ShowAdminNotice => !CanInstall;
    public string AdminNotice => AppEnvironment.AdminNotice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(AvailableVersionText), nameof(Notes), nameof(ShowBanner))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private UpdateInfo? _available;

    [ObservableProperty]
    private string _statusText = "Noch nicht gesucht.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _bannerDismissed;

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(DownloadCommand))]
    [NotifyPropertyChangedFor(nameof(IsVerifiedReady))]
    private bool _isVerified;

    public bool IsAvailable => Available is not null;
    public bool IsVerifiedReady => IsVerified;
    public string AvailableVersionText => Available is null ? "" : $"Version {Available.Version} ist verfügbar.";
    public string Notes => Available?.Notes is { Length: > 0 } n ? n : "Keine Änderungsnotizen angegeben.";
    public bool ShowBanner => IsAvailable && !BannerDismissed;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckAsync(CancellationToken ct)
    {
        StatusText = "Suche nach Updates …";
        try
        {
            var r = await _service.CheckAsync(ct);
            Available = r.Available;
            StatusText = r.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Suche abgebrochen.";
        }
        catch (Exception ex)
        {
            FileLog.Warn("Update-Prüfung fehlgeschlagen", ex);
            StatusText = "Update-Prüfung fehlgeschlagen.";
        }
    }

    bool CanDownload() => CanInstall && IsAvailable && !IsVerified;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync(CancellationToken ct)
    {
        if (Available is not { } info) return;
        DownloadPercent = 0;
        StatusText = "Update wird heruntergeladen …";
        try
        {
            var progress = new Progress<double>(p => DownloadPercent = Math.Clamp(p * 100, 0, 100));
            _verified = await _service.DownloadAndVerifyAsync(info, progress, ct);
            IsVerified = true;
            StatusText = $"Version {_verified.Version} ist heruntergeladen, Signatur und Prüfsumme stimmen.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download abgebrochen.";
        }
        catch (Exception ex) when (ex is UpdateVerificationException or UpdateDownloadException)
        {
            FileLog.Warn("Update abgelehnt", ex);
            StatusText = ex.Message;
            _dialogs.Inform("Update nicht installierbar", ex.Message + " Die heruntergeladenen Dateien wurden gelöscht.");
        }
        catch (Exception ex)
        {
            FileLog.Error("Update-Download fehlgeschlagen", ex);
            StatusText = "Download fehlgeschlagen.";
        }
    }

    bool CanInstallNow() => CanInstall && IsVerified;

    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private void Install()
    {
        if (_verified is not { } u) return;
        if (!_dialogs.Confirm("Jetzt installieren",
                "WinWächter wird beendet, das Setup installiert die neue Version und startet das Programm danach neu.",
                "Installieren"))
            return;
        try
        {
            _service.Launch(u);   // the verified file stays locked until the setup is started
        }
        catch (Exception ex)
        {
            FileLog.Error("Setup-Start fehlgeschlagen", ex);
            _dialogs.Inform("Setup-Start fehlgeschlagen", ex.Message);
            return;
        }
        finally
        {
            u.Dispose();
            _verified = null;
            IsVerified = false;
        }
        _shutdown();
    }

    [RelayCommand]
    private void DismissBanner() => BannerDismissed = true;
}
