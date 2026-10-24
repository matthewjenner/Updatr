namespace Updatr.Core.Sources;

/// <summary>
/// Orders version strings the way package ecosystems mean them: dotted numeric parts compared as
/// numbers (<c>1.10</c> &gt; <c>1.9</c>), missing parts as zero (<c>1.2</c> = <c>1.2.0</c>), and a
/// pre-release (<c>2.0.0-beta.1</c>) before its release. Build metadata (<c>+abc</c>) is ignored.
/// </summary>
/// <remarks>
/// Good enough for npm, NuGet, PyPI, PowerShell Gallery, and VS Code versions - all dotted numbers
/// with an optional pre-release tag. Not a full PEP 440 implementation (epochs, post-releases);
/// those are rare and compare sensibly enough as text tails.
/// </remarks>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? candidate, string? installed)
        => !string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(installed)
            && Instance.Compare(candidate, installed) > 0;

    /// <summary>True when the version has no pre-release tag.</summary>
    public static bool IsStable(string version) => Split(version).PreRelease.Length == 0;

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        (string[] xs, string xPre) = Split(x);
        (string[] ys, string yPre) = Split(y);

        for (int i = 0; i < Math.Max(xs.Length, ys.Length); i++)
        {
            int c = ComparePart(i < xs.Length ? xs[i] : "0", i < ys.Length ? ys[i] : "0");

            if (c != 0)
                return c;
        }

        // Same numbers: a release sorts after any of its pre-releases.
        if (xPre.Length == 0 || yPre.Length == 0)
            return (xPre.Length == 0 ? 1 : 0) - (yPre.Length == 0 ? 1 : 0);

        string[] xp = xPre.Split('.');
        string[] yp = yPre.Split('.');

        for (int i = 0; i < Math.Max(xp.Length, yp.Length); i++)
        {
            if (i >= xp.Length)
                return -1;
            if (i >= yp.Length)
                return 1;

            int c = ComparePart(xp[i], yp[i]);

            if (c != 0)
                return c;
        }

        return 0;
    }

    private static (string[] Parts, string PreRelease) Split(string version)
    {
        string v = version.Trim().TrimStart('v', 'V');
        int plus = v.IndexOf('+', StringComparison.Ordinal);

        if (plus >= 0)
            v = v[..plus];

        int dash = v.IndexOf('-', StringComparison.Ordinal);
        string pre = dash >= 0 ? v[(dash + 1)..] : string.Empty;
        string main = dash >= 0 ? v[..dash] : v;

        return (main.Split('.'), pre);
    }

    /// <summary>Numeric when both are numbers; otherwise ordinal, with numbers before text.</summary>
    private static int ComparePart(string a, string b)
    {
        bool aNum = long.TryParse(a, out long an);
        bool bNum = long.TryParse(b, out long bn);

        if (aNum && bNum)
            return an.CompareTo(bn);
        if (aNum != bNum)
            return aNum ? -1 : 1;

        return string.CompareOrdinal(a, b);
    }
}
