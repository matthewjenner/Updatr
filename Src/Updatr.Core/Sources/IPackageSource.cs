using Updatr.Core.Operations;

namespace Updatr.Core.Sources;

/// <summary>
/// One ecosystem Updatr can list and change packages in (winget now; npm, pipx, ... later). The
/// operations only ever talk to this, so every source gets the same Apps screen and CLI.
/// </summary>
public interface IPackageSource
{
    /// <summary>Source id used in keys, hold rules, and the UI ("winget").</summary>
    string Id { get; }

    /// <summary>
    /// Streams what is installed, as soon as each part is known. Must not throw for a single bad
    /// package: list it with <see cref="InstalledPackage.CheckProblem"/> set instead. Progress
    /// (e.g. "checked 12 of 40 groups") goes to <paramref name="sink"/>.
    /// </summary>
    IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, CancellationToken cancellationToken);

    /// <summary>Finds one installed package by id without listing everything. Null if not installed.</summary>
    Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Upgrades one package. Never throws for an install failure - the outcome says what happened.
    /// Runs to completion once started: installers are not interrupted mid-way.
    /// </summary>
    Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken);

    /// <summary>Installs the same package again over itself (repair a broken install).</summary>
    Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the package through its own uninstaller (winget, the registered uninstall command,
    /// or the ecosystem's tool). Leftovers are not touched here.
    /// </summary>
    Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken);
}

/// <summary>Waits until no other installer is running, so Updatr never collides with one (MSI error 1618).</summary>
public interface IInstallerGate
{
    /// <summary>Returns when idle. Reports what it is waiting for through the sink while it waits.</summary>
    Task WaitUntilIdleAsync(IOperationSink sink, string itemId, CancellationToken cancellationToken);
}
