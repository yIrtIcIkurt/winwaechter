using System.IO;
using System.Text.Json;
using WinWaechter.Core.Logging;

namespace WinWaechter.App.Services;

/// <summary>User settings stored as JSON in the data folder.</summary>
public sealed class AppSettings
{
    public const int MinAblageDays = 1;
    public const int MaxAblageDays = 365;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool CheckUpdatesOnStart { get; set; } = true;
    public int AblageDays { get; set; } = 14;

    /// <summary>The weekly check (scheduled task) is switched on. Off until the user switches it on.</summary>
    public bool MaintenanceEnabled { get; set; }
    public DayOfWeek MaintenanceDay { get; set; } = DayOfWeek.Sunday;
    /// <summary>„HH:mm“, a full hour from the settings list.</summary>
    public string MaintenanceTime { get; set; } = DefaultMaintenanceTime;
    /// <summary>The one-time hint on the overview was answered with „Nein danke“.</summary>
    public bool MaintenanceHintDismissed { get; set; }

    public const string DefaultMaintenanceTime = "11:00";

    public TimeOnly MaintenanceTimeOfDay =>
        TimeOnly.TryParseExact(MaintenanceTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
            out var t) ? t : new TimeOnly(11, 0);

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.AblageDays = Math.Clamp(s.AblageDays, MinAblageDays, MaxAblageDays);
                if (!Enum.IsDefined(s.MaintenanceDay)) s.MaintenanceDay = DayOfWeek.Sunday;
                if (!TimeOnly.TryParseExact(s.MaintenanceTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _))
                    s.MaintenanceTime = DefaultMaintenanceTime;
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            FileLog.Warn("Einstellungen nicht lesbar, verwende Standardwerte", ex);
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Warn("Einstellungen konnten nicht gespeichert werden", ex);
        }
    }
}
