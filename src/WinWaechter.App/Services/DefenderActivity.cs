using CommunityToolkit.Mvvm.ComponentModel;

namespace WinWaechter.App.Services;

/// <summary>
/// One Defender job at a time across pages. Schutz keeps its own operation state (wired in as <see cref="Others"/>);
/// the one-off Defender actions (ActionRunner) and the file check on Sicherheit register here.
/// UI thread only.
/// </summary>
public sealed class DefenderActivity : ObservableObject
{
    public const string BusyText = "Defender ist gerade beschäftigt (Scan oder Aktualisierung). Bitte warten Sie, bis der Vorgang fertig ist.";

    int _running;

    /// <summary>The Schutz page's own Defender operation (ProtectViewModel.IsDefenderBusy).</summary>
    public Func<bool> Others { get; set; } = () => false;

    /// <summary>A job registered here (action or file check) is running.</summary>
    public bool IsRunning => _running > 0;

    /// <summary>Any Defender job, here or on the Schutz page.</summary>
    public bool IsBusy => IsRunning || Others();

    /// <summary>Registers a job; false when Defender is busy (nothing registered then).</summary>
    public bool TryBegin()
    {
        if (IsBusy) return false;
        _running++;
        OnPropertyChanged(nameof(IsRunning));
        return true;
    }

    public void End()
    {
        if (_running == 0) return;
        _running--;
        OnPropertyChanged(nameof(IsRunning));
    }
}
