using System.Globalization;

namespace PSRemoteSub.Core.Library;

/// <summary>A library version <c>YYYYMMDD.N</c>; compares by date, then N.</summary>
public readonly record struct LibraryVersion(long Date, int N) : IComparable<LibraryVersion>
{
    public int CompareTo(LibraryVersion other)
    {
        var c = Date.CompareTo(other.Date);
        return c != 0 ? c : N.CompareTo(other.N);
    }

    public static bool operator >(LibraryVersion a, LibraryVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(LibraryVersion a, LibraryVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(LibraryVersion a, LibraryVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(LibraryVersion a, LibraryVersion b) => a.CompareTo(b) <= 0;

    public static bool TryParse(string? s, out LibraryVersion v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split('.');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var date)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
        v = new LibraryVersion(date, n);
        return true;
    }

    public override string ToString() => $"{Date}.{N}";
}
