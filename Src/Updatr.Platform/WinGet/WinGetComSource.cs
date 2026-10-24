using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Management.Deployment;
using Updatr.Core.Diagnostics;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.WinGet;

/// <summary>
/// winget through its COM API - structured data, no text parsing. The primary winget source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Listing is partitioned.</b> One bad manifest in the community index makes a single "list
/// everything" query fail outright (<c>0x80070057</c>, reproduced in Phase 0). So installed packages
/// are queried in groups by the first letter of their name, in parallel; results stream out per
/// group; a failing group is split by its first two letters; and only the packages in a group that
/// still fails are listed with "could not check". One broken entry costs a handful of rows.
/// </para>
/// <para>
/// <b>Machine-wide packages upgrade in the elevated broker</b> when one is available: one UAC
/// prompt per session instead of one per installer. A declined prompt, or an installer that refuses
/// to run elevated, falls back to running here (the installer may then prompt by itself).
/// </para>
/// </remarks>
public sealed class WinGetComSource(ElevationService? elevation, int parallelism = 8) : IPackageSource
{
    public const string SourceId = "winget";

    /// <summary>Broker request type for an elevated upgrade/reinstall.</summary>
    public const string BrokerRunType = "winget-run";

    private const uint ProhibitsElevation = 0x8A150056;

    public string Id => SourceId;

    public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<InstalledPackage> found = Channel.CreateUnbounded<InstalledPackage>();
        Task producer = Task.Run(() => ProduceAsync(found.Writer, sink, cancellationToken), cancellationToken);

        await foreach (InstalledPackage package in found.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return package;

        await producer.ConfigureAwait(false);
    }

    public Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            CatalogPackage? package = FindCatalogPackage(WinGetRuntime.CreatePackageManager(), id, out FindPackagesResultStatus status);

            if (status != FindPackagesResultStatus.Ok)
                throw new PackageSourceException($"winget could not look up '{id}' ({status}) - its index may have a broken entry for this package.");

            return package is null ? null : Map(package);
        }, cancellationToken);

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(package, reinstall: false, sink, cancellationToken);

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(package, reinstall: true, sink, cancellationToken);

    private async Task<PackageOutcome> RunAsync(InstalledPackage package, bool reinstall, IOperationSink sink, CancellationToken cancellationToken)
    {
        string itemId = package.Key.ToString();

        if (elevation is not null && (package.RunsElevated || package.Scope == InstallScope.Machine))
        {
            BrokerClient? broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

            if (broker is not null)
            {
                PackageOutcome elevated = await RunInBrokerAsync(broker, package, reinstall, sink, cancellationToken).ConfigureAwait(false);

                if (elevated.Code?.Code != ProhibitsElevation)
                    return elevated;

                sink.Report(new LogLine(LogLevel.Info, $"{package.Name}: the installer refuses to run elevated; running it normally", itemId));
            }
            else
            {
                sink.Report(new LogLine(LogLevel.Warning, $"{package.Name}: administrator access was declined; the installer may ask for it itself", itemId));
            }
        }

        return await Task.Run(() => RunLocalAsync(package.Id, reinstall, package.HasUnknownVersion, itemId, sink), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Upgrades or reinstalls in this process. Also what the broker runs, elevated. Waits for the
    /// installer to finish; never interrupts it.
    /// </summary>
    public static async Task<PackageOutcome> RunLocalAsync(string id, bool reinstall, bool unknownVersion, string itemId, IOperationSink sink)
    {
        PackageManager manager;

        try
        {
            manager = WinGetRuntime.CreatePackageManager();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return PackageOutcome.Failed($"winget is not available: {ex.Message}");
        }

        CatalogPackage? package = FindCatalogPackage(manager, id, out FindPackagesResultStatus status);

        if (status != FindPackagesResultStatus.Ok)
            return PackageOutcome.Failed($"winget could not look up this package ({status}); its index may have a broken entry for it");

        if (package is null)
            return PackageOutcome.Failed("winget no longer finds this package installed");

        InstallOptions options = new()
        {
            PackageInstallMode = PackageInstallMode.Silent,
            AcceptPackageAgreements = true,
            AllowUpgradeToUnknownVersion = unknownVersion,
            Force = reinstall,
        };

        string note = string.Empty;

        if (reinstall && package.InstalledVersion is { } installed)
        {
            // Reinstall the version that is there, not silently upgrade.
            PackageVersionId? same = package.AvailableVersions.FirstOrDefault(v => v.Version == installed.Version);

            if (same is not null)
                options.PackageVersionId = same;
            else
                note = $" (version {installed.Version} is no longer offered; installed the current version)";
        }

        var operation = reinstall ? manager.InstallPackageAsync(package, options) : manager.UpgradePackageAsync(package, options);
        operation.Progress = (_, progress) => sink.Report(ToProgress(progress, itemId));

        InstallResult result = await operation.AsTask().ConfigureAwait(false);
        PackageOutcome outcome = Map(result);

        return note.Length > 0 && outcome.Kind == OutcomeKind.Ok ? outcome with { Detail = outcome.Detail + note } : outcome;
    }

    private async Task ProduceAsync(ChannelWriter<InstalledPackage> output, IOperationSink sink, CancellationToken cancellationToken)
    {
        try
        {
            PackageManager manager = WinGetRuntime.CreatePackageManager();
            List<LocalEntry> local = ReadLocal(manager);

            // Group by the first character of the installed name - the partitions are built from
            // what is actually installed, so together they cover everything.
            List<IGrouping<string, LocalEntry>> groups = [.. local.Where(l => l.Name.Length > 0).GroupBy(l => Prefix(l.Name, 1)).OrderBy(g => g.Key, StringComparer.Ordinal)];
            HashSet<PackageKey> emitted = [];
            Lock gate = new();
            int done = 0;

            async ValueTask Emit(InstalledPackage package)
            {
                lock (gate)
                {
                    if (!emitted.Add(package.Key))
                        return;
                }

                await output.WriteAsync(package, cancellationToken).ConfigureAwait(false);
            }

            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (group, ct) =>
            {
                if (!await TryQueryAsync(group.Key, Emit, ct).ConfigureAwait(false))
                {
                    // Split the failing group by its first two letters and try each part.
                    foreach (IGrouping<string, LocalEntry> part in group.GroupBy(l => Prefix(l.Name, 2)))
                    {
                        if (await TryQueryAsync(part.Key, Emit, ct).ConfigureAwait(false))
                            continue;

                        DiagLog.WinGetPartitionFailed(part.Key, part.Count());

                        foreach (LocalEntry entry in part)
                            await Emit(entry.ToUncheckedPackage()).ConfigureAwait(false);
                    }
                }

                int completed = Interlocked.Increment(ref done);
                sink.Report(new ProgressReported(null, (double)completed / groups.Count, Message: $"winget: checked {completed} of {groups.Count} groups"));
            }).ConfigureAwait(false);

            output.Complete();
        }
        catch (Exception ex)
        {
            output.Complete(ex);
        }
    }

    private static async Task<bool> TryQueryAsync(string prefix, Func<InstalledPackage, ValueTask> emit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A composite catalog per query: they are cheap, and independent ones parallelize cleanly.
        PackageCatalog catalog = ConnectComposite(WinGetRuntime.CreatePackageManager());
        FindPackagesOptions options = new();
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Name, Option = PackageFieldMatchOption.StartsWithCaseInsensitive, Value = prefix });
        FindPackagesResult result = catalog.FindPackages(options);

        if (result.Status != FindPackagesResultStatus.Ok)
            return false;

        IReadOnlyList<MatchResult> matches = result.Matches;

        for (int i = 0; i < matches.Count; i++)
        {
            InstalledPackage? mapped = TryMap(matches[i].CatalogPackage);

            if (mapped is not null)
                await emit(mapped).ConfigureAwait(false);
        }

        return true;
    }

    private static PackageCatalog ConnectComposite(PackageManager manager)
    {
        CreateCompositePackageCatalogOptions options = new() { CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs };
        IReadOnlyList<PackageCatalogReference> catalogs = manager.GetPackageCatalogs();

        for (int i = 0; i < catalogs.Count; i++)
        {
            bool isExplicit = false;

            try
            {
                isExplicit = catalogs[i].Info.Explicit;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // Older servers lack the property: treat as not explicit.
            }

            // Explicit catalogs (winget-font) only take part when named, and break composite search.
            if (!isExplicit)
                options.Catalogs.Add(catalogs[i]);
        }

        ConnectResult connect = manager.CreateCompositePackageCatalog(options).Connect();

        return connect.Status == ConnectResultStatus.Ok
            ? connect.PackageCatalog
            : throw new PackageSourceException($"winget could not open its sources ({connect.Status})");
    }

    private static CatalogPackage? FindCatalogPackage(PackageManager manager, string id, out FindPackagesResultStatus status)
    {
        FindPackagesOptions options = new();
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
        FindPackagesResult result = ConnectComposite(manager).FindPackages(options);
        status = result.Status;

        if (status != FindPackagesResultStatus.Ok)
            return null;

        IReadOnlyList<MatchResult> matches = result.Matches;

        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].CatalogPackage.InstalledVersion is not null)
                return matches[i].CatalogPackage;
        }

        return null;
    }

    private static List<LocalEntry> ReadLocal(PackageManager manager)
    {
        ConnectResult connect = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages).Connect();

        if (connect.Status != ConnectResultStatus.Ok)
            throw new PackageSourceException($"winget could not read installed packages ({connect.Status})");

        FindPackagesResult result = connect.PackageCatalog.FindPackages(new FindPackagesOptions());

        if (result.Status != FindPackagesResultStatus.Ok)
            throw new PackageSourceException($"winget could not read installed packages ({result.Status})");

        List<LocalEntry> entries = [];
        IReadOnlyList<MatchResult> matches = result.Matches;

        for (int i = 0; i < matches.Count; i++)
        {
            CatalogPackage p = matches[i].CatalogPackage;
            entries.Add(new LocalEntry(p.Name ?? string.Empty, p.Id, p.InstalledVersion?.Version ?? string.Empty, p.InstalledVersion?.Publisher));
        }

        return entries;
    }

    private static InstalledPackage? TryMap(CatalogPackage package)
    {
        try
        {
            return Map(package);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null; // One unreadable entry must not sink its group.
        }
    }

    private static InstalledPackage Map(CatalogPackage package)
    {
        PackageVersionInfo? installed = package.InstalledVersion;
        PackageVersionInfo? offered = package.DefaultInstallVersion;
        string scope = installed?.GetMetadata(PackageVersionMetadataField.InstalledScope) ?? string.Empty;
        string? managedBy = LauncherDetector.ManagedBy(
            installed?.GetMetadata(PackageVersionMetadataField.StandardUninstallCommand),
            installed?.GetMetadata(PackageVersionMetadataField.InstalledLocation),
            package.Id);

        // A newer version winget will not apply in place - almost always because the vendor switched
        // installer technology (Phase 3 finding: Audacity exe -> WiX, Wave Link MSI -> MSIX). The
        // winget CLI lists these as upgrades that then fail; listing them as "needs a fresh install"
        // is the honest version.
        string? freshInstall = null, freshReason = null;

        if (!package.IsUpdateAvailable && installed is not null && offered is not null
            && installed.CompareToVersion(offered.Version) == CompareResult.Lesser)
        {
            freshInstall = offered.Version;
            freshReason = DescribeTechnologyChange(installed, offered);
        }

        return new InstalledPackage
        {
            ManagedBy = managedBy,
            NewerVersionNeedsFreshInstall = freshInstall,
            FreshInstallReason = freshReason,
            Source = SourceId,
            Id = package.Id,
            Name = package.Name,
            InstalledVersion = installed?.Version ?? string.Empty,
            AvailableVersion = package.IsUpdateAvailable ? offered?.Version : null,
            Catalog = offered?.PackageCatalog?.Info?.Name,
            Publisher = installed?.Publisher,
            Scope = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase) ? InstallScope.Machine
                : scope.Equals("User", StringComparison.OrdinalIgnoreCase) ? InstallScope.User
                : InstallScope.Unknown,
            RunsElevated = package.IsUpdateAvailable && NeedsElevation(scope, offered),
            IsManaged = offered is not null,
        };
    }

    /// <summary>
    /// Whether the upgrade should run in the elevated broker. winget rarely knows the installed
    /// scope (Phase 3: nearly every package reported none), so this asks about the installer it
    /// would actually run: a machine-scope installer, or one that needs or requests elevation.
    /// Running those from the broker means one UAC prompt per session instead of one per installer.
    /// Only computed for packages with an update - it is a cross-process call per package.
    /// </summary>
    private static bool NeedsElevation(string installedScope, PackageVersionInfo? offered)
    {
        if (installedScope.Equals("Machine", StringComparison.OrdinalIgnoreCase))
            return true;

        if (installedScope.Equals("User", StringComparison.OrdinalIgnoreCase) || offered is null)
            return false;

        try
        {
            PackageInstallerInfo? installer = offered.GetApplicableInstaller(new InstallOptions());

            return installer is not null
                && (installer.Scope == PackageInstallerScope.System
                    || installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    private static string DescribeTechnologyChange(PackageVersionInfo installed, PackageVersionInfo offered)
    {
        string from = installed.GetMetadata(PackageVersionMetadataField.InstallerType) ?? string.Empty;
        string? to = null;

        try
        {
            to = offered.GetApplicableInstaller(new InstallOptions())?.InstallerType.ToString();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // No applicable installer for this machine - covered by the generic wording below.
        }

        return from.Length > 0 && to is not null && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            ? $"it switches installer type ({from.ToLowerInvariant()} to {to.ToLowerInvariant()}), so winget cannot upgrade in place"
            : "winget will not apply it as an in-place upgrade";
    }

    private static PackageOutcome Map(InstallResult result)
    {
        long extended = result.ExtendedErrorCode?.HResult ?? 0;

        return result.Status switch
        {
            InstallResultStatus.Ok => result.RebootRequired
                ? new PackageOutcome(OutcomeKind.Ok, "Reboot required to finish", RebootRequired: true)
                : PackageOutcome.Ok(),
            InstallResultStatus.NoApplicableUpgrade => new PackageOutcome(OutcomeKind.AlreadyCurrent, "No applicable upgrade (already current)"),
            InstallResultStatus.InstallError when result.InstallerErrorCode != 0 => Prefix(PackageOutcome.FromCode(result.InstallerErrorCode), "installer: "),
            _ when extended != 0 => Prefix(PackageOutcome.FromCode(extended), $"{result.Status}: "),
            _ => PackageOutcome.Failed(result.Status.ToString()),
        };

        static PackageOutcome Prefix(PackageOutcome outcome, string prefix) => outcome with { Detail = prefix + outcome.Detail };
    }

    private static ProgressReported ToProgress(InstallProgress progress, string itemId) => progress.State switch
    {
        PackageInstallProgressState.Queued => new ProgressReported(itemId, null, Message: "Queued"),
        PackageInstallProgressState.Downloading => new ProgressReported(itemId, progress.DownloadProgress,
            Bytes: (long)progress.BytesDownloaded, TotalBytes: progress.BytesRequired > 0 ? (long)progress.BytesRequired : null, Message: "Downloading"),
        PackageInstallProgressState.Installing => new ProgressReported(itemId, progress.InstallationProgress > 0 ? progress.InstallationProgress : null, Message: "Installing"),
        PackageInstallProgressState.PostInstall => new ProgressReported(itemId, 1, Message: "Finishing"),
        _ => new ProgressReported(itemId, 1, Message: "Done"),
    };

    private static async Task<PackageOutcome> RunInBrokerAsync(BrokerClient broker, InstalledPackage package, bool reinstall, IOperationSink sink, CancellationToken cancellationToken)
    {
        string itemId = package.Key.ToString();
        JsonObject request = new() { ["id"] = package.Id, ["reinstall"] = reinstall, ["unknownVersion"] = package.HasUnknownVersion };

        try
        {
            JsonNode? reply = await broker.SendAsync(BrokerRunType, request, e => sink.Report(BrokerProgress.FromJson(e, itemId)), CancellationToken.None).ConfigureAwait(false);
            return BrokerOutcome.FromJson(reply);
        }
        catch (BrokerException ex)
        {
            return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
        }
    }

    private static string Prefix(string name, int length)
        => name.Length <= length ? name.ToLowerInvariant() : name[..length].ToLowerInvariant();

    /// <summary>An installed entry from the local catalog, before any update check.</summary>
    private sealed record LocalEntry(string Name, string Id, string Version, string? Publisher)
    {
        public InstalledPackage ToUncheckedPackage() => new()
        {
            Source = SourceId,
            Id = Id,
            Name = Name,
            InstalledVersion = Version,
            Publisher = Publisher,
            IsManaged = false,
            CheckProblem = "winget could not check this for updates - its index has a broken entry in this group",
        };
    }
}

/// <summary>A source could not answer at all (as opposed to one package failing).</summary>
public sealed class PackageSourceException(string message) : Exception(message);
