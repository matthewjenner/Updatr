using System.Runtime.InteropServices;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.Windows;
using Updatr.Platform.WinGet;
using Windows.ApplicationModel;
using WinRtPackageManager = Windows.Management.Deployment.PackageManager;

namespace Updatr.Platform.Debloat;

/// <summary>
/// Store (Appx / MSIX) apps through the Windows package manager API - no PowerShell. Removal for the
/// current user runs here; removing the app from the image for new users (deprovisioning) needs
/// administrator access and goes through the broker. Revert reinstalls from the Microsoft Store.
/// </summary>
public sealed class AppxTarget(ElevationService elevation, WinGetSource winget, IProcessRunner runner) : IDebloatTarget, IAppxInventory
{
    /// <summary>The package list is read once per status pass, not once per action.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private (DateTime At, IReadOnlyList<UserPackage> Packages)? _cache;

    public bool Handles(DebloatAction action) => action is AppxAction;

    public bool NeedsAdmin(DebloatAction action) => !((AppxAction)action).KeepProvisioned;

    public Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        AppxAction a = (AppxAction)action;
        WildcardPattern pattern = new(a.Name);
        UserPackage[] installed = [.. Packages().Where(p => pattern.IsMatch(p.Name))];

        if (installed.Length > 0)
            return Task.FromResult(new ActionStatus(ActionState.NotApplied, "Installed: " + string.Join(", ", installed.Select(p => p.DisplayName).Distinct(StringComparer.Ordinal))));

        // Listing the image's provisioned apps needs administrator access; unelevated, "not
        // installed for you" is the best answer available.
        if (!a.KeepProvisioned && WindowsInfo.IsElevated && ProvisionedNames(pattern) is { Count: > 0 })
            return Task.FromResult(new ActionStatus(ActionState.NotApplied, "Still in the Windows image for new users"));

        return Task.FromResult(new ActionStatus(ActionState.Applied));
    }

    public Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        WildcardPattern pattern = new(((AppxAction)action).Name);
        return Task.FromResult(new ActionBackup { Action = action, Existed = true, Packages = [.. Packages().Where(p => pattern.IsMatch(p.Name)).Select(p => p.FullName)] });
    }

    public async Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        AppxAction a = (AppxAction)action;
        WildcardPattern pattern = new(a.Name);
        List<PackageOutcome> outcomes = [];
        WinRtPackageManager manager = new();

        foreach (UserPackage package in Packages().Where(p => pattern.IsMatch(p.Name)))
        {
            sink.Report(new ProgressReported(itemId, Message: $"Removing {package.DisplayName}"));

            try
            {
                await manager.RemovePackageAsync(package.FullName).AsTask(CancellationToken.None).ConfigureAwait(false);
                outcomes.Add(PackageOutcome.Ok());
            }
            catch (COMException ex)
            {
                outcomes.Add(PackageOutcome.FromCode(unchecked((uint)ex.HResult)) with { Detail = $"Could not remove {package.DisplayName}: {ex.Message.Trim()}" });
            }
        }

        Invalidate();

        if (!a.KeepProvisioned)
        {
            PackageOutcome deprovision = await DebloatElevation.DeprovisionAsync(elevation, a, () => DeprovisionLocalAsync(a)).ConfigureAwait(false);

            // Removed for this user but not from the image: still worth reporting, not a failure.
            outcomes.Add(deprovision.Kind == OutcomeKind.Skipped
                ? PackageOutcome.Skipped("not removed for new users (administrator access declined)")
                : deprovision);
        }

        return outcomes.Count == 0 ? PackageOutcome.Ok() : DebloatEngine.Combine(outcomes);
    }

    public async Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        AppxAction a = (AppxAction)backup.Action;

        if (backup.Packages.Count == 0)
            return new PackageOutcome(OutcomeKind.AlreadyCurrent, $"{a.Name} was not installed for you, so there is nothing to reinstall");

        if (a.StoreId is null)
            return PackageOutcome.Skipped($"Reinstall {a.Name} from the Microsoft Store if you want it back");

        sink.Report(new ProgressReported(itemId, Message: $"Reinstalling {a.Name} from the Microsoft Store"));
        PackageOutcome outcome = await winget.InstallNewAsync(new WingetSourceRef { Id = a.StoreId, Source = "msstore" }, itemId, sink, runner).ConfigureAwait(false);
        Invalidate();
        return outcome;
    }

    public Task<IReadOnlyList<StoreApp>> ListRemovableAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<StoreApp>>([.. Packages()
            .Where(p => p.StoreSigned)
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new StoreApp(p.Name, p.DisplayName, p.Publisher))]);

    /// <summary>Removes matching apps from the Windows image (new users). Runs elevated.</summary>
    internal static async Task<PackageOutcome> DeprovisionLocalAsync(AppxAction action)
    {
        WildcardPattern pattern = new(action.Name);
        WinRtPackageManager manager = new();
        List<string> failures = [];

        foreach (string family in ProvisionedNames(pattern))
        {
            try
            {
                await manager.DeprovisionPackageForAllUsersAsync(family).AsTask(CancellationToken.None).ConfigureAwait(false);
            }
            catch (COMException ex)
            {
                failures.Add($"{family}: {ex.Message.Trim()}");
            }
        }

        return failures.Count == 0 ? PackageOutcome.Ok() : PackageOutcome.Failed("Could not remove from the Windows image: " + string.Join("; ", failures));
    }

    /// <summary>Family names of provisioned packages matching the pattern. Empty when listing is not allowed.</summary>
    private static IReadOnlyList<string> ProvisionedNames(WildcardPattern pattern)
    {
        try
        {
            return [.. new WinRtPackageManager().FindProvisionedPackages()
                .Select(p => p.Id)
                .Where(id => pattern.IsMatch(id.Name))
                .Select(id => id.FamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<UserPackage> Packages()
    {
        lock (_gate)
        {
            if (_cache is { } cached && DateTime.UtcNow - cached.At < CacheFor)
                return cached.Packages;
        }

        List<UserPackage> packages = [];

        foreach (Package package in new WinRtPackageManager().FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage)
                    continue;

                PackageId id = package.Id;
                packages.Add(new UserPackage(id.Name, id.FullName, DisplayNameOf(package, id.Name), PublisherOf(package),
                    package.SignatureKind == PackageSignatureKind.Store && !package.IsBundle));
            }
            catch (COMException)
            {
                // A package mid-update or damaged: leave it out of this pass.
            }
        }

        lock (_gate)
            _cache = (DateTime.UtcNow, packages);

        return packages;
    }

    private void Invalidate()
    {
        lock (_gate)
            _cache = null;
    }

    private static string DisplayNameOf(Package package, string fallback)
    {
        try
        {
            string name = package.DisplayName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? fallback : name;
        }
        catch (COMException)
        {
            return fallback;
        }
    }

    private static string? PublisherOf(Package package)
    {
        try
        {
            return package.PublisherDisplayName;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private sealed record UserPackage(string Name, string FullName, string DisplayName, string? Publisher, bool StoreSigned);
}
