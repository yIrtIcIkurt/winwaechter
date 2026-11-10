using System.Globalization;

namespace WinWaechter.Core.Model;

/// <summary>German byte-size text such as "13,5 GB".</summary>
public static class SizeText
{
    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Format(long bytes)
    {
        string[] units = ["Byte", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} Byte" : string.Format(De, "{0:0.#} {1}", v, units[i]);
    }
}
