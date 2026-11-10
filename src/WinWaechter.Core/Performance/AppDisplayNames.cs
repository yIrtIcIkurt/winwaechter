using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Performance;

/// <summary>Turns a package family name ("Name_publisherid") into the name a user knows.</summary>
public interface IAppDisplayNames
{
    /// <summary>The display name from the package repository; false when it cannot be resolved (no guessing).</summary>
    bool TryResolve(string packageFamilyName, [NotNullWhen(true)] out string? name);
}

public sealed class WinAppDisplayNames(IRegistry reg, Func<string, string?>? resolver = null) : IAppDisplayNames
{
    static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(60);
    readonly Func<string, string?> _load = resolver ?? Load;
    IReadOnlyList<string>? _packages;
    DateTime _packagesAt;
    bool _enumLogged;

    const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    static string Fallback(string family)
    {
        var i = family.IndexOf('_');
        return i > 0 ? family[..i] : family;
    }

    /// <summary>The display name, or the family name up to the first "_" when it cannot be resolved.</summary>
    public string Resolve(string packageFamilyName) =>
        TryResolve(packageFamilyName, out var name) ? name : Fallback(packageFamilyName);

    public bool TryResolve(string packageFamilyName, [NotNullWhen(true)] out string? name)
    {
        name = null;
        try
        {
            var us = packageFamilyName.LastIndexOf('_');
            if (us <= 0 || us == packageFamilyName.Length - 1) return false;
            var namePart = packageFamilyName[..us];
            var publisher = packageFamilyName[(us + 1)..];

            foreach (var sub in PackageNames())
            {
                if (!sub.StartsWith(namePart + "_", StringComparison.OrdinalIgnoreCase)
                    || !sub.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase)) continue;
                if (reg.Get(Hive.CurrentUser, Packages + "\\" + sub, "DisplayName") is not string raw || raw.Length == 0) continue;

                var text = Expand(raw, sub);
                if (!string.IsNullOrWhiteSpace(text)) { name = text; return true; }
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Anzeigename einer App nicht lesbar: " + packageFamilyName, ex);
        }
        return false;
    }

    // The package list is read once a minute, not once per app; a failure is logged once.
    IReadOnlyList<string> PackageNames()
    {
        if (_packages is not null && DateTime.UtcNow - _packagesAt < CacheAge) return _packages;
        try
        {
            _packages = reg.SubKeyNames(Hive.CurrentUser, Packages);
        }
        catch (Exception ex)
        {
            if (!_enumLogged) { _enumLogged = true; FileLog.Warn("App-Paketliste nicht lesbar", ex); }
            _packages = [];
        }
        _packagesAt = DateTime.UtcNow;
        return _packages;
    }

    string? Expand(string raw, string fullName)
    {
        if (raw.StartsWith("@{", StringComparison.Ordinal)) return _load(raw);
        if (raw.StartsWith("ms-resource://", StringComparison.OrdinalIgnoreCase)) return _load($"@{{{fullName}?{raw}}}");
        if (raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            var key = raw["ms-resource:".Length..].TrimStart('/');
            var us = fullName.IndexOf('_');
            var packageName = us > 0 ? fullName[..us] : fullName;
            return _load($"@{{{fullName}?ms-resource://{packageName}/resources/{key}}}");
        }
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
