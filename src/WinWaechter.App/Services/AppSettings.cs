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

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                s.AblageDays = Math.Clamp(s.AblageDays, MinAblageDays, MaxAblageDays);
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
