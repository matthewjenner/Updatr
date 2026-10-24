using System.Text.RegularExpressions;

namespace Updatr.Core.Catalog;

/// <summary>
/// Case-insensitive whole-string wildcard match: <c>*</c> is any run of characters (including
/// none), <c>?</c> is exactly one. Everything else is literal. Same semantics as PowerShell's
/// <c>-like</c> for the patterns the hold list uses, without its <c>[a-z]</c> classes.
/// </summary>
public sealed class WildcardPattern
{
    private readonly Regex _regex;

    public WildcardPattern(string pattern)
    {
        Pattern = pattern;
        string body = Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal);
        _regex = new Regex($"^{body}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    public bool IsMatch(string value) => _regex.IsMatch(value);
}
