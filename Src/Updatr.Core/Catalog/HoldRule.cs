using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Updatr.Core.Catalog;

/// <summary>What a matching hold rule does to a package with an update available.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldBehavior>))]
public enum HoldBehavior
{
    /// <summary>Shown, but skipped by "upgrade all" unless explicitly included.</summary>
    [JsonStringEnumMemberName("hold")]
    Hold,

    /// <summary>
    /// Listed as upgradable but updated by the Microsoft Store, not the source's CLI (App
    /// Installer). Shown as "updates via Store" instead of failing every run.
    /// </summary>
    [JsonStringEnumMemberName("storeManaged")]
    StoreManaged,

    /// <summary>
    /// The app updates itself (launchers, Discord). Skipped by "upgrade all"; upgraded only when
    /// named. Mirrors winget's <c>RequireExplicitUpgrade</c> manifest flag, which its COM API does
    /// not expose.
    /// </summary>
    [JsonStringEnumMemberName("explicit")]
    Explicit,
}

/// <summary>A pattern of package ids that should not be upgraded in passing.</summary>
/// <remarks>
/// Guiding rule (from <c>Docs/legacy-script.md</c>): hold a package only when an in-place upgrade
/// can cross a breaking boundary - the major version is not pinned in the id - or when it is
/// stateful, pinned by other tools, or best left to the installers that bundle it.
/// </remarks>
[Description("Packages to leave alone during \"upgrade all\".")]
public sealed record HoldRule
{
    [Description("Package id wildcard: * matches any run of characters, ? one character. Case-insensitive.")]
    public required string Pattern { get; init; }

    [Description("Source the pattern applies to (winget, npm, ...). Defaults to winget.")]
    public string Source { get; init => field = value ?? "winget"; } = "winget";

    [Description("Grouping shown in the UI, e.g. \"Databases\".")]
    public required string Group { get; init; }

    [Description("Why this is held - shown to the user.")]
    public required string Reason { get; init; }

    [Description("hold (default), storeManaged, or explicit (self-updating: only upgraded when named).")]
    public HoldBehavior Behavior { get; init; } = HoldBehavior.Hold;
}
