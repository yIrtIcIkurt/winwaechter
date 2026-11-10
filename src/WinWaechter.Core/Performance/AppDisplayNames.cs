using System.Runtime.InteropServices;
using System.Text;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>Turns a package family name ("Name_publisherid") into the name a user knows.</summary>
public interface IAppDisplayNames
{
    string Resolve(string packageFamilyName);
}

public sealed class WinAppDisplayNames(IRegistry reg) : IAppDisplayNames
{
    const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    static string Fallback(string family)
    {
        var i = family.IndexOf('_');
        return i > 0 ? family[..i] : family;
    }

    public string Resolve(string packageFamilyName)
    {
        var fallback = Fallback(packageFamilyName);
        try
        {
            var us = packageFamilyName.LastIndexOf('_');
            if (us <= 0 || us == packageFamilyName.Length - 1) return fallback;
            var namePart = packageFamilyName[..us];
            var publisher = packageFamilyName[(us + 1)..];

            foreach (var sub in reg.SubKeyNames(Hive.CurrentUser, Packages))
            {
                if (!sub.StartsWith(namePart + "_", StringComparison.OrdinalIgnoreCase)
                    || !sub.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase)) continue;
                if (reg.Get(Hive.CurrentUser, Packages + "\\" + sub, "DisplayName") is not string raw || raw.Length == 0) continue;

                var text = Expand(raw, sub);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Anzeigename einer App nicht lesbar: " + packageFamilyName, ex);
        }
        return fallback;
    }

    static string? Expand(string raw, string fullName)
    {
        if (raw.StartsWith("@{", StringComparison.Ordinal)) return Load(raw);
        if (raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return Load($"@{{{fullName}? {raw}}}");
        return raw;
    }

    static string? Load(string source)
    {
        try
        {
            var sb = new StringBuilder(260);
            return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
