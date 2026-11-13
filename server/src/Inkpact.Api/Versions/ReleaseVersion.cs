using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Versions;

/// <summary>
/// A semantic version (run B.8), as the app is stamped with (<c>0.1.0</c>, <c>0.1.0+edge.abc1234</c>, <c>0.0.0-dev</c>)
/// and as a release tag names one (<c>v0.1.0</c>). Ordered by semver's precedence: build metadata (after <c>+</c>) does
/// not count, so an <c>edge</c> build ranks the same as the release it is built on, and a pre-release ranks below its
/// release.
/// </summary>
public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<ReleaseVersion>
{
    /// <summary>A build that was not stamped with a version: a developer's, or a test's.</summary>
    public bool IsDevelopment => Major == 0 && Minor == 0 && Patch == 0;

    public bool IsPreRelease => PreRelease.Length > 0;

    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d{0,8})\.(?<minor>0|[1-9]\d{0,8})\.(?<patch>0|[1-9]\d{0,8})(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (text is null || text.Length > 128)
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var pre = match.Groups["pre"].Value;
        // Semver: a numeric identifier has no leading zero.
        if (pre.Split('.').Any(id => id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = new ReleaseVersion(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            pre);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A version without a pre-release ranks above one with it.
        if (!IsPreRelease || !other.IsPreRelease)
        {
            return other.IsPreRelease.CompareTo(IsPreRelease);
        }

        var mine = PreRelease.Split('.');
        var theirs = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var order = CompareIdentifiers(mine[i], theirs[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    /// <summary>Numbers compare as numbers and below words; words compare by their characters (ASCII).</summary>
    private static int CompareIdentifiers(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        return (aNumeric, bNumeric) switch
        {
            (true, true) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(a, b),
        };
    }

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        PreRelease.Length > 0 ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}
