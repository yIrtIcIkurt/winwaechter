using CommunityToolkit.Mvvm.ComponentModel;

namespace WinWaechter.App.Services;

/// <summary>
/// One long job of a kind at a time (program updates, repairs, disk checks), outside the app-wide change
/// gate so other pages stay usable. UI thread only.
/// </summary>
public sealed class ActivityLock(string busyText) : ObservableObject
{
    public const string Updates = "updates";
    public const string Repair = "repair";
    public const string Disk = "disk";

    int _running;

    public string BusyText { get; } = busyText;

    public bool IsRunning => _running > 0;

    /// <summary>Registers a job; false when one is already running.</summary>
    public bool TryBegin()
    {
        if (IsRunning) return false;
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

    /// <summary>The three locks of 0.6 with their busy texts.</summary>
    public static Dictionary<string, ActivityLock> CreateDefaults() => new(StringComparer.Ordinal)
    {
        [Updates] = new ActivityLock("Es werden gerade Programme aktualisiert."),
        [Repair] = new ActivityLock("Eine Reparatur läuft gerade."),
        [Disk] = new ActivityLock("Das Laufwerk wird gerade geprüft oder optimiert."),
    };
}

/// <summary>Restore point before a final action: a recent one (Windows keeps one per 24 h) is fine; on a
/// real failure the user decides.</summary>
public static class RestorePointGate
{
    public static async Task<bool> EnsureAsync(Func<string, Core.Optimizer.RestorePointResult> create, string title, IDialogService dialogs)
    {
        // WMI off the UI thread; the question comes back on the caller's (UI) context
        var r = await Task.Run(() => create(title));
        if (r.Created || r.RecentExists) return true;
        // Computerschutz off: say so and where to switch it on
        var why = r.ProtectionOff ? " " + r.Message : "";
        return dialogs.Ask(title, $"Es konnte kein Wiederherstellungspunkt angelegt werden.{why} Trotzdem fortfahren?",
            "Trotzdem fortfahren", "Abbrechen");
    }
}
