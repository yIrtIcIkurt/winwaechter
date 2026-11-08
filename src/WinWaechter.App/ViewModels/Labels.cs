using System.Globalization;
using WinWaechter.Core.Model;

namespace WinWaechter.App.ViewModels;

/// <summary>Visual tone of a chip or status mark; the views map it to theme colours.</summary>
public enum Tone { Neutral, Calm, Good, Watch, Alarm }

/// <summary>German display texts for Core enums and values.</summary>
public static class Labels
{
    public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Rating(Rating r) => r switch
    {
        Core.Model.Rating.Info => "Info",
        Core.Model.Rating.Auffaellig => "Auffällig",
        Core.Model.Rating.Verdaechtig => "Verdächtig",
        Core.Model.Rating.Entbehrlich => "Entbehrlich",
        Core.Model.Rating.Unerwuenscht => "Unerwünscht",
        _ => r.ToString(),
    };

    // Only "Verdächtig" gets the alarm colour; unknown is never scary.
    public static Tone RatingTone(Rating r) => r switch
    {
        Core.Model.Rating.Verdaechtig => Tone.Alarm,
        Core.Model.Rating.Auffaellig or Core.Model.Rating.Unerwuenscht => Tone.Watch,
        Core.Model.Rating.Entbehrlich => Tone.Calm,
        _ => Tone.Neutral,
    };

    public static string Status(ChangeStatus s) => s switch
    {
        ChangeStatus.Pending => "Ausstehend",
        ChangeStatus.Applied => "Angewendet",
        ChangeStatus.Failed => "Fehlgeschlagen",
        ChangeStatus.Undone => "Rückgängig gemacht",
        ChangeStatus.SkippedDrift => "Übersprungen",
        _ => s.ToString(),
    };

    public static Tone StatusTone(ChangeStatus s) => s switch
    {
        ChangeStatus.Applied => Tone.Calm,
        ChangeStatus.Failed => Tone.Alarm,
        ChangeStatus.SkippedDrift => Tone.Watch,
        _ => Tone.Neutral,
    };

    public static string Size(long bytes)
    {
        string[] units = ["Byte", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} Byte" : string.Format(De, "{0:0.#} {1}", v, units[i]);
    }

    public static string Date(DateTimeOffset? d) =>
        d is { } v ? v.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", De) : "–";

    public static string Days(int days) => days switch
    {
        0 => "heute",
        1 => "1 Tag",
        _ => $"{days} Tage",
    };

    public static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", De) : t.ToString(@"mm\:ss", De);
}
