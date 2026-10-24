using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Core.Debloat;

/// <summary>Live state of one action on this machine.</summary>
public enum ActionState
{
    /// <summary>The machine already looks the way the action would leave it.</summary>
    Applied,

    /// <summary>The action would change something.</summary>
    NotApplied,

    /// <summary>Nothing to act on here (the service or task does not exist on this edition).</summary>
    NotApplicable,

    /// <summary>Could not tell (access denied, API missing). Applying is still attempted.</summary>
    Unknown,
}

/// <summary>What detection found for one action.</summary>
public sealed record ActionStatus(ActionState State, string? Detail = null);

/// <summary>Live state of a whole item, from its actions.</summary>
public enum ItemState
{
    Applied,
    Partial,
    NotApplied,
    Unknown,

    /// <summary>Not for this version of Windows (<see cref="DebloatItem.MinBuild"/>).</summary>
    Unavailable,
}

/// <summary>
/// One kind of change (registry value, Store app, service, scheduled task). Implementations live in
/// Platform and decide for themselves when a change needs the elevated broker.
/// </summary>
public interface IDebloatTarget
{
    bool Handles(DebloatAction action);

    /// <summary>Whether applying it needs administrator access - used to decide on a restore point first.</summary>
    bool NeedsAdmin(DebloatAction action);

    /// <summary>Reads the live state. Never changes anything.</summary>
    Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken);

    /// <summary>Records what is there now, so it can be put back. Called (and saved) before <see cref="ApplyAsync"/>.</summary>
    Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken);

    /// <summary>Makes the change. Never throws for an expected failure; the outcome says what happened.</summary>
    Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken);

    /// <summary>Puts back what <see cref="CaptureAsync"/> recorded.</summary>
    Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken);
}

/// <summary>A Store app on this machine that the user could remove.</summary>
/// <param name="Name">Package name, e.g. Microsoft.BingNews.</param>
/// <param name="DisplayName">What the Start menu calls it.</param>
/// <param name="Publisher">Publisher display name, when known.</param>
public sealed record StoreApp(string Name, string DisplayName, string? Publisher);

/// <summary>The removable Store apps installed for the current user.</summary>
public interface IAppxInventory
{
    /// <summary>Store-signed, non-framework, non-resource packages. System and sideloaded packages are excluded.</summary>
    Task<IReadOnlyList<StoreApp>> ListRemovableAsync(CancellationToken cancellationToken);
}

/// <summary>Best-effort System Restore point before machine-wide changes.</summary>
public interface IRestorePoint
{
    /// <summary>Returns null when one was created, otherwise why not (throttled, disabled, declined).</summary>
    Task<string?> CreateAsync(string description, CancellationToken cancellationToken);
}

/// <summary>One item's live status.</summary>
/// <param name="Item">The rule.</param>
/// <param name="State">Overall state.</param>
/// <param name="Actions">Per action, in the item's order.</param>
/// <param name="Drifted">Applied after the last run, not applied now - Windows (or someone) put it back.</param>
public sealed record DebloatItemStatus(DebloatItem Item, ItemState State, IReadOnlyList<ActionStatus> Actions, bool Drifted)
{
    /// <summary>Applying would change something.</summary>
    public bool NeedsWork => State is ItemState.NotApplied or ItemState.Partial or ItemState.Unknown;
}

/// <summary>A Store app no rule knows and nobody kept.</summary>
/// <param name="Kept">On the user's keep list (shown so it can be released).</param>
/// <param name="IsNew">Not seen at the last run.</param>
public sealed record UnrecognizedApp(StoreApp App, bool Kept, bool IsNew);

/// <summary>Everything the Debloat screen and <c>debloat status</c> show.</summary>
/// <param name="LastRun">When debloat was last applied or reverted, if ever.</param>
public sealed record DebloatReport(IReadOnlyList<DebloatItemStatus> Items, IReadOnlyList<UnrecognizedApp> Unrecognized, DateTimeOffset? LastRun);

/// <summary>What to apply: the recommended set, named items, and/or unrecognized apps to remove.</summary>
public sealed record DebloatApplyRequest
{
    public bool Recommended { get; init; }

    public IReadOnlyList<string> ItemIds { get; init; } = [];

    /// <summary>Package names from the unrecognized list, removed as one-off items.</summary>
    public IReadOnlyList<string> RemoveApps { get; init; } = [];
}
