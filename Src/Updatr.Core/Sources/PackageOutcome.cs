using Updatr.Core.Errors;

namespace Updatr.Core.Sources;

/// <summary>How one package action ended.</summary>
public enum OutcomeKind
{
    /// <summary>The action did what was asked.</summary>
    Ok,

    /// <summary>
    /// Nothing to apply. Common when a previous run already upgraded the package but the source
    /// still lists an update. Never counted as a failure.
    /// </summary>
    AlreadyCurrent,

    /// <summary>The action failed; <see cref="PackageOutcome.Detail"/> says why.</summary>
    Failed,

    /// <summary>
    /// Not attempted - held, canceled before it started, declined elevation, or a prerequisite
    /// failed. <see cref="PackageOutcome.Detail"/> says which.
    /// </summary>
    Skipped,
}

/// <summary>
/// The result of one install / upgrade / uninstall. A real failure is always distinguishable from
/// a no-op, and the decoded code is always kept.
/// </summary>
/// <param name="Kind">How the action ended.</param>
/// <param name="Detail">Human-readable explanation; empty for a plain success.</param>
/// <param name="RebootRequired">True when a reboot is needed (or was started) to finish.</param>
/// <param name="Code">The decoded code behind the outcome, when there was one.</param>
public sealed record PackageOutcome(OutcomeKind Kind, string Detail, bool RebootRequired = false, ErrorCodeInfo? Code = null)
{
    public static PackageOutcome Ok() => new(OutcomeKind.Ok, string.Empty);

    public static PackageOutcome Skipped(string reason) => new(OutcomeKind.Skipped, reason);

    public static PackageOutcome Failed(string reason) => new(OutcomeKind.Failed, reason);

    /// <summary>
    /// Builds the outcome from an exit code or HRESULT. Zero is success; codes that mean "nothing
    /// to do" become <see cref="OutcomeKind.AlreadyCurrent"/>; "succeeded, reboot needed" codes are
    /// <see cref="OutcomeKind.Ok"/> with <see cref="RebootRequired"/> set.
    /// </summary>
    public static PackageOutcome FromCode(long code)
    {
        if (code == 0)
            return Ok();

        ErrorCodeInfo info = ErrorCodes.Describe(code);

        return info.Outcome switch
        {
            CodeOutcome.AlreadyCurrent => new PackageOutcome(OutcomeKind.AlreadyCurrent, info.ToString(), Code: info),
            CodeOutcome.SuccessRebootRequired => new PackageOutcome(OutcomeKind.Ok, info.ToString(), RebootRequired: true, Code: info),
            _ => new PackageOutcome(OutcomeKind.Failed, info.ToString(), Code: info),
        };
    }
}
