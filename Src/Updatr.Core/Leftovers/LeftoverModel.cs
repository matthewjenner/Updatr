using System.Text.Json.Serialization;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Core.Leftovers;

/// <summary>What kind of thing a leftover is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LeftoverKind>))]
public enum LeftoverKind
{
    Folder,
    RegistryKey,
    Service,
    ScheduledTask,
    Shortcut,
    FirewallRule,
}

/// <summary>How sure Updatr is that a leftover belonged to the removed app. Nothing is ever pre-selected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Confidence>))]
public enum Confidence
{
    /// <summary>A name or wording match only.</summary>
    Low,

    /// <summary>Named exactly after the app (or its publisher\app) in a place apps keep their data.</summary>
    Medium,

    /// <summary>The app's own install folder or uninstall entry, something pointing into it, or created when Updatr installed it.</summary>
    High,
}

/// <summary>One thing the uninstaller left behind.</summary>
/// <param name="Id">Stable within its scan ("1", "2", ...), for the CLI.</param>
/// <param name="Location">
/// Folder or shortcut: full path. Registry key: <c>HKLM\SOFTWARE\...</c> / <c>HKCU\Software\...</c>.
/// Service: service name. Task: full task path. Firewall rule: rule name.
/// </param>
/// <param name="Reason">Why it is listed, for the user.</param>
/// <param name="SizeBytes">Folder size, when known.</param>
/// <param name="NeedsAdmin">Moving it needs administrator access.</param>
public sealed record Leftover(string Id, LeftoverKind Kind, string Location, Confidence Confidence, string Reason, long? SizeBytes = null, bool NeedsAdmin = false);

/// <summary>
/// What is known about an app before its uninstaller runs - afterwards its registration is gone.
/// Captured by <see cref="ILeftoverScanner.DescribeAsync"/>.
/// </summary>
public sealed record LeftoverTarget
{
    public required PackageKey Package { get; init; }

    public required string Name { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Install folder from the app's registration or winget.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>Folder of the registered uninstaller, when it is not msiexec.</summary>
    public string? UninstallerFolder { get; init; }

    /// <summary>The app's own Uninstall key (<c>HKLM\SOFTWARE\...\Uninstall\{id}</c>), if found.</summary>
    public string? UninstallKey { get; init; }

    /// <summary>
    /// The registered uninstaller, when its file is gone - deleted by hand, or on a drive whose
    /// letter now belongs to another drive. No uninstaller can run; only the registration is left.
    /// </summary>
    public string? MissingUninstaller { get; init; }

    /// <summary>What Updatr recorded when it installed the app, if it did.</summary>
    public IReadOnlyList<FootprintItem> Footprint { get; init => field = value ?? []; } = [];
}

/// <summary>One thing an install created, from the before/after snapshot.</summary>
public sealed record FootprintItem(LeftoverKind Kind, string Location);

/// <summary>Leftovers found after one uninstall, waiting for the user to review.</summary>
public sealed record LeftoverScan
{
    /// <summary>File-safe id: timestamp plus app slug.</summary>
    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required LeftoverTarget Target { get; init; }

    public IReadOnlyList<Leftover> Items { get; init => field = value ?? []; } = [];
}

/// <summary>Finds what an uninstaller left behind. Platform implementation; never changes anything.</summary>
public interface ILeftoverScanner
{
    /// <summary>Captures the app's details while it is still installed. Null when the source has nothing to scan (npm, VS Code, ...).</summary>
    Task<LeftoverTarget?> DescribeAsync(InstalledPackage package, CancellationToken cancellationToken);

    /// <summary>Looks for leftovers of an app that has just been uninstalled.</summary>
    Task<IReadOnlyList<Leftover>> ScanAsync(LeftoverTarget target, CancellationToken cancellationToken);

    /// <summary>The items that are still there. Uninstallers that finish in the background remove things after the scan.</summary>
    Task<IReadOnlyList<Leftover>> StillPresentAsync(IReadOnlyList<Leftover> items, CancellationToken cancellationToken);
}

/// <summary>A quarantined set of leftovers, restorable until purged.</summary>
/// <param name="Id">File-safe id (folder name under the machine's quarantine folder).</param>
/// <param name="App">The app they came from.</param>
/// <param name="Items">What was moved, with the outcome per item.</param>
/// <param name="SizeBytes">Space it takes on the drive.</param>
public sealed record QuarantineEntry(string Id, DateTimeOffset At, string App, IReadOnlyList<QuarantinedItem> Items, long SizeBytes);

/// <summary>One leftover in quarantine.</summary>
/// <param name="Stored">Whether it was moved in (and so can be restored).</param>
public sealed record QuarantinedItem(LeftoverKind Kind, string Location, bool Stored, string? Problem = null);

/// <summary>Moves leftovers out of the way, restorably. Platform implementation (files, registry, admin).</summary>
public interface IQuarantine
{
    /// <summary>Moves the leftovers into a new entry. Each item is saved before it is removed; a failed save leaves it in place.</summary>
    Task<QuarantineEntry> MoveAsync(string app, IReadOnlyList<Leftover> items, IOperationSink sink, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Puts everything in an entry back where it was. Existing files are not overwritten.</summary>
    Task<PackageOutcome> RestoreAsync(string entryId, IOperationSink sink, CancellationToken cancellationToken);

    /// <summary>Deletes an entry for good.</summary>
    Task PurgeAsync(string entryId, CancellationToken cancellationToken);
}

/// <summary>Before/after snapshots around installs Updatr runs, so their leftovers are known exactly.</summary>
public interface IFootprintRecorder
{
    /// <summary>Takes the "before" snapshot. The returned state goes back to <see cref="AfterAsync"/>.</summary>
    Task<object?> BeforeAsync(CancellationToken cancellationToken);

    /// <summary>Takes the "after" snapshot and saves the difference for the package.</summary>
    Task AfterAsync(object? before, PackageKey package, CancellationToken cancellationToken);

    /// <summary>What was recorded for the package on this machine, or empty.</summary>
    Task<IReadOnlyList<FootprintItem>> LoadAsync(PackageKey package, CancellationToken cancellationToken);
}
