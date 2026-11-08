using System.Globalization;

namespace WinWaechter.Core.Update;

// Plain major.minor.patch version. A leading "v" is accepted; pre-release tags are not.
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] == 'v') s = s[1..];
        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        var n = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || parts[i].Length > 9) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        }
        v = new SemVer(n[0], n[1], n[2]);
        return true;
    }

    public int CompareTo(SemVer o)
    {
        var c = Major.CompareTo(o.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(o.Minor);
        return c != 0 ? c : Patch.CompareTo(o.Patch);
    }

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
