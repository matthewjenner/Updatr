namespace Updatr.Core.Sources;

/// <summary>Identifies a package across sources: the source id plus the package's id there.</summary>
public readonly record struct PackageKey(string Source, string Id)
{
    public override string ToString() => $"{Source}:{Id}";
}

/// <summary>Whether a package installed for one user or the whole machine. Decides elevation.</summary>
public enum InstallScope
{
    Unknown,
    User,
    Machine,
}

/// <summary>Something installed on this machine, as one source sees it.</summary>
public sealed record InstalledPackage
{
    /// <summary>Source that manages it ("winget"). Hold rules match on this.</summary>
    public required string Source { get; init; }

    /// <summary>Package id within the source (e.g. <c>Git.Git</c>), or a local-only id for apps no catalog knows.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Installed version as reported; "Unknown" or empty when the source cannot tell.</summary>
    public required string InstalledVersion { get; init; }

    /// <summary>Newest version on offer, when an update is available.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>
    /// A newer version exists but cannot be applied as an in-place upgrade - typically because the
    /// new version uses a different installer technology (exe to MSI, MSI to MSIX). winget would
    /// fail with <c>0x8A15008E</c>; it needs an uninstall and a fresh install instead.
    /// </summary>
    public string? NewerVersionNeedsFreshInstall { get; init; }

    /// <summary>Why <see cref="NewerVersionNeedsFreshInstall"/> cannot be applied in place, for the user.</summary>
    public string? FreshInstallReason { get; init; }

    /// <summary>The source's own catalog the update would come from ("winget", "msstore"), if any.</summary>
    public string? Catalog { get; init; }

    public string? Publisher { get; init; }

    public InstallScope Scope { get; init; } = InstallScope.Unknown;

    /// <summary>
    /// The upgrade's installer is machine-wide or needs elevation, so it runs in the elevated
    /// broker (one UAC prompt per session) rather than prompting by itself.
    /// </summary>
    public bool RunsElevated { get; init; }

    /// <summary>
    /// False when no catalog knows this app (installed by hand, a game launcher's own updater, ...):
    /// it is listed, but Updatr cannot upgrade or reinstall it.
    /// </summary>
    public bool IsManaged { get; init; } = true;

    /// <summary>
    /// Set when the source could not check this package for updates (e.g. a broken winget index
    /// entry). The package is listed; its update status is unknown.
    /// </summary>
    public string? CheckProblem { get; init; }

    /// <summary>
    /// The game launcher that owns this install ("Steam", "Battle.net", ...), or null. Launcher-managed
    /// apps are listed but never changed through winget - see <see cref="LauncherDetector"/>.
    /// </summary>
    public string? ManagedBy { get; init; }

    public PackageKey Key => new(Source, Id);

    public bool HasUpdate => AvailableVersion is not null;

    /// <summary>Installed version the source cannot read - upgrades from it are unreliable.</summary>
    public bool HasUnknownVersion => string.IsNullOrWhiteSpace(InstalledVersion)
        || InstalledVersion.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
}
