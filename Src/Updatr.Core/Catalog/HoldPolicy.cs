namespace Updatr.Core.Catalog;

/// <summary>Why a package is held.</summary>
/// <param name="Rule">The rule that matched.</param>
/// <param name="IsUserRule">True when the rule came from the user's own hold list, not the catalog.</param>
public sealed record HoldMatch(HoldRule Rule, bool IsUserRule);

/// <summary>
/// Decides whether a package is held. Catalog rules apply unless the user released them; the
/// user's own holds always apply.
/// </summary>
/// <remarks>
/// Precedence, highest first: a user hold, then a user release (cancels catalog rules only), then
/// catalog rules in file order. A release cannot cancel a user hold - holding and releasing the
/// same package yourself means you want it held.
/// </remarks>
public sealed class HoldPolicy
{
    private readonly List<(HoldRule Rule, WildcardPattern Pattern)> _catalog;
    private readonly List<(HoldRule Rule, WildcardPattern Pattern)> _userHolds;
    private readonly List<(string Source, WildcardPattern Pattern)> _userReleases;

    /// <param name="catalogRules">Rules from the catalog's holds.json.</param>
    /// <param name="userHolds">Extra holds the user added.</param>
    /// <param name="userReleases">(source, pattern) pairs the user un-held from the catalog rules.</param>
    public HoldPolicy(
        IEnumerable<HoldRule> catalogRules,
        IEnumerable<HoldRule>? userHolds = null,
        IEnumerable<(string Source, string Pattern)>? userReleases = null)
    {
        _catalog = [.. catalogRules.Select(r => (r, new WildcardPattern(r.Pattern)))];
        _userHolds = [.. (userHolds ?? []).Select(r => (r, new WildcardPattern(r.Pattern)))];
        _userReleases = [.. (userReleases ?? []).Select(r => (r.Source, new WildcardPattern(r.Pattern)))];
    }

    /// <summary>Returns the rule holding this package, or null when it is free to upgrade.</summary>
    /// <param name="source">Source id, e.g. "winget".</param>
    /// <param name="packageId">The package id within that source.</param>
    public HoldMatch? Match(string source, string packageId)
    {
        foreach ((HoldRule rule, WildcardPattern pattern) in _userHolds)
        {
            if (SameSource(rule.Source, source) && pattern.IsMatch(packageId))
                return new HoldMatch(rule, IsUserRule: true);
        }

        foreach ((string releaseSource, WildcardPattern pattern) in _userReleases)
        {
            if (SameSource(releaseSource, source) && pattern.IsMatch(packageId))
                return null;
        }

        foreach ((HoldRule rule, WildcardPattern pattern) in _catalog)
        {
            if (SameSource(rule.Source, source) && pattern.IsMatch(packageId))
                return new HoldMatch(rule, IsUserRule: false);
        }

        return null;
    }

    private static bool SameSource(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
