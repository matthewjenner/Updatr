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
/// are queried in groups by the first letter of their name; results stream out per group; a failing
/// group is split by its first two letters; and only the packages in a group that still fails are
/// listed with "could not check". One broken entry costs a handful of rows.
/// </para>
/// <para>
/// <b>Gently, though.</b> Two workers, each with one catalog connection for the whole listing.
/// Phase 9 finding: three bugchecks (0x153, a Filter Manager lock leaked when a thread of winget's
/// COM server exited) each followed a listing that ran eight parallel queries, each opening its own
/// composite catalog - a burst of index-file opens and server worker threads. The kernel bug is
/// not ours, but this is the load that set it off.
/// </para>
/// <para>
/// <b>Machine-wide packages upgrade in the elevated broker</b> when one is available: one UAC
/// prompt per session instead of one per installer. A declined prompt, or an installer that refuses
/// to run elevated, falls back to running here (the installer may then prompt by itself).
/// </para>
/// </remarks>
public sealed class WinGetComSource(ElevationService? elevation, int parallelism = WinGetComSource.DefaultParallelism) : IPackageSource
{
    public const string SourceId = "winget";

    /// <summary>Concurrent queries against winget's COM server while listing. Keep low - see the remarks.</summary>
    public const int DefaultParallelism = 2;

    /// <summary>Broker request type for an elevated upgrade/reinstall.</summary>
    public const string BrokerRunType = "winget-run";

    private const uint ProhibitsElevation = 0x8A150056;

    public string Id => SourceId;

    public IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, CancellationToken cancellationToken)
        => ListAsync(parallelism, sink, cancellationToken);

    /// <summary>One worker, one catalog connection: the background check's listing (see Known edges in the workplan).</summary>
    public IAsyncEnumerable<InstalledPackage> ListInstalledQuietlyAsync(IOperationSink sink, CancellationToken cancellationToken)
        => ListAsync(1, sink, cancellationToken);

    private async IAsyncEnumerable<InstalledPackage> ListAsync(int workers, IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<InstalledPackage> found = Channel.CreateUnbounded<InstalledPackage>();
        Task producer = Task.Run(() => ProduceAsync(found.Writer, workers, sink, cancellationToken), cancellationToken);

        await foreach (InstalledPackage package in found.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return package;

        await producer.ConfigureAwait(false);
    }

    /// <summary>One lookup at a time, through one catalog connection reused for a while (see the remarks).</summary>
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private static readonly TimeSpan LookupConnectionLifetime = TimeSpan.FromMinutes(5);
    private PackageCatalog? _lookupCatalog;
    private DateTime _lookupCatalogAt;

    public async Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
    {
        await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(() =>
            {
                if (_lookupCatalog is null || DateTime.UtcNow - _lookupCatalogAt > LookupConnectionLifetime)
                {
                    _lookupCatalog = ConnectComposite(WinGetRuntime.CreatePackageManager());
                    _lookupCatalogAt = DateTime.UtcNow;
                }

                CatalogPackage? package;
                FindPackagesResultStatus status;

                try
                {
                    package = FindCatalogPackage(_lookupCatalog, id, out status);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // The server recycled the connection: drop it, and let the next lookup reconnect.
                    _lookupCatalog = null;
                    throw;
                }

                if (status != FindPackagesResultStatus.Ok)
                    throw new PackageSourceException($"winget could not look up '{id}' ({status}) - its index may have a broken entry for this package.");

                return package is null ? null : Map(package);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lookupGate.Release();
        }
    }

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(package, reinstall: false, sink, cancellationToken);

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(package, reinstall: true, sink, cancellationToken);

    /// <summary>
    /// Uninstalls through winget, which runs the app's registered (quiet) uninstaller - also for
    /// apps no winget catalog knows. Machine-wide installs go through the broker, like upgrades.
    /// </summary>
    public async Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
    {
        try
        {
            return await UninstallCoreAsync(package, sink, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ForgetLookupConnection();
        }
    }

    private async Task<PackageOutcome> UninstallCoreAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
    {
        string itemId = package.Key.ToString();
        bool triedElevated = false;

        if (elevation is not null && await Task.Run(() => RemovalNeedsElevation(package.Id), CancellationToken.None).ConfigureAwait(false))
        {
            triedElevated = true;

            if (await UninstallInBrokerAsync(package, sink, cancellationToken).ConfigureAwait(false) is { } elevated)
                return elevated;
        }

        PackageOutcome outcome = await Task.Run(() => RunUninstallLocalAsync(package.Id, itemId, sink), CancellationToken.None).ConfigureAwait(false);

        // winget often cannot say where an app was installed. A permission failure from the
        // uninstaller (Phase 7: a per-machine MSI reporting no scope gave 1603) means it needed
        // administrator rights after all - try once more through the broker.
        if (!triedElevated && elevation is not null && !Windows.WindowsInfo.IsElevated && outcome.Code?.Code is { } code && PermissionCodes.Contains(code))
        {
            sink.Report(new LogLine(LogLevel.Info, $"{package.Name}: the uninstaller needs administrator access; retrying elevated", itemId));

            if (await UninstallInBrokerAsync(package, sink, cancellationToken).ConfigureAwait(false) is { } elevated)
                return elevated;
        }

        return outcome;
    }

    /// <summary>Uninstaller results that mean "not allowed" rather than "broken": MSI 1603 / 1625 / 1730, access denied.</summary>
    private static readonly HashSet<uint> PermissionCodes = [1603, 1625, 1730, 0x80070005];

    /// <summary>The broker's outcome, or null when elevation was declined or the uninstaller refuses to run elevated.</summary>
    private async Task<PackageOutcome?> UninstallInBrokerAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
    {
        string itemId = package.Key.ToString();
        BrokerClient? broker = await elevation!.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

        if (broker is null)
        {
            sink.Report(new LogLine(LogLevel.Warning, $"{package.Name}: administrator access was declined; the uninstaller may ask for it itself", itemId));
            return null;
        }

        try
        {
            PackageOutcome elevated = BrokerOutcome.FromJson(await broker.SendAsync(BrokerRunType, new JsonObject { ["id"] = package.Id, ["mode"] = "uninstall" },
                e => sink.Report(BrokerProgress.FromJson(e, itemId)), CancellationToken.None).ConfigureAwait(false));

            return elevated.Code?.Code == ProhibitsElevation ? null : elevated;
        }
        catch (BrokerException ex)
        {
            return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
        }
    }

    /// <summary>Uninstalls in this process. Also what the broker runs for "uninstall".</summary>
    public static async Task<PackageOutcome> RunUninstallLocalAsync(string id, string itemId, IOperationSink sink)
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
            return new PackageOutcome(OutcomeKind.AlreadyCurrent, "Not installed any more");

        var operation = manager.UninstallPackageAsync(package, new UninstallOptions { PackageUninstallMode = PackageUninstallMode.Silent });
        operation.Progress = (_, progress) => sink.Report(progress.State switch
        {
            PackageUninstallProgressState.Uninstalling => new ProgressReported(itemId, progress.UninstallationProgress > 0 ? progress.UninstallationProgress : null, Message: "Uninstalling"),
            PackageUninstallProgressState.PostUninstall => new ProgressReported(itemId, 1, Message: "Finishing"),
            _ => new ProgressReported(itemId, null, Message: "Queued"),
        });

        UninstallResult result = await operation.AsTask().ConfigureAwait(false);
        long extended = result.ExtendedErrorCode?.HResult ?? 0;

        return result.Status switch
        {
            UninstallResultStatus.Ok => result.RebootRequired
                ? new PackageOutcome(OutcomeKind.Ok, "Reboot required to finish", RebootRequired: true)
                : PackageOutcome.Ok(),
            UninstallResultStatus.UninstallError when result.UninstallerErrorCode != 0
                => InstallerOutcome(result.UninstallerErrorCode, extended, "uninstaller"),
            _ when extended != 0 => Prefixed(PackageOutcome.FromCode(extended), $"{result.Status}: "),
            _ => PackageOutcome.Failed(result.Status.ToString()),
        };

        static PackageOutcome Prefixed(PackageOutcome outcome, string prefix) => outcome with { Detail = prefix + outcome.Detail };
    }

    /// <summary>
    /// Whether removing it should run elevated: a machine-scope install, a machine uninstall
    /// registration (by local id or by MSI product code under HKLM), or an install folder or
    /// uninstaller under Program Files / Windows. Per-user and Store apps never do.
    /// </summary>
    private static bool RemovalNeedsElevation(string id)
    {
        if (id.StartsWith(@"ARP\Machine\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (id.StartsWith(@"ARP\User\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            PackageVersionInfo? installed = FindCatalogPackage(WinGetRuntime.CreatePackageManager(), id, out _)?.InstalledVersion;

            if (installed is null)
                return false;

            string scope = installed.GetMetadata(PackageVersionMetadataField.InstalledScope) ?? string.Empty;

            if (scope.Length > 0)
                return ParseScope(scope) == InstallScope.Machine;

            // MSI installs: the product code's registration says which hive it lives in.
            // Index, never enumerate: CsWinRT's IEnumerable view of these lists fails to cast.
            IReadOnlyList<string> codes = installed.ProductCodes;

            for (int i = 0; i < codes.Count; i++)
            {
                if (MachineUninstallEntryExists(codes[i]))
                    return true;
            }

            string[] machineRoots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ];

            string location = installed.GetMetadata(PackageVersionMetadataField.InstalledLocation) ?? string.Empty;
            string uninstaller = (installed.GetMetadata(PackageVersionMetadataField.StandardUninstallCommand) ?? string.Empty).TrimStart('"');

            // An uninstaller under Program Files is a machine install; msiexec lives under Windows
            // for every MSI, so the uninstall command only counts when it is not msiexec.
            return (location.Length > 0 && machineRoots.Any(r => r.Length > 0 && location.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                || (!uninstaller.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
                    && machineRoots.Take(2).Any(r => r.Length > 0 && uninstaller.StartsWith(r, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is PackageSourceException or System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException)
        {
            return false; // The uninstall itself will report the real problem.
        }
    }

    /// <summary>What winget knows about an installed package, for the leftover scan. Null when not found.</summary>
    public static InstalledDetails? Describe(string id)
    {
        CatalogPackage? package = FindCatalogPackage(WinGetRuntime.CreatePackageManager(), id, out _);

        if (package?.InstalledVersion is not { } installed)
            return null;

        // Index, never enumerate (CsWinRT list views fail to cast).
        IReadOnlyList<string> codes = installed.ProductCodes;
        List<string> productCodes = [];

        for (int i = 0; i < codes.Count; i++)
            productCodes.Add(codes[i]);

        return new InstalledDetails(
            package.Name,
            installed.Publisher,
            installed.GetMetadata(PackageVersionMetadataField.InstalledLocation),
            installed.GetMetadata(PackageVersionMetadataField.StandardUninstallCommand),
            productCodes);
    }

    /// <summary>
    /// The outcome of an installer's exit code. Package manifests map vendor exit codes to meanings
    /// (OBS: 6 = "in use"); winget reports that as the extended code. When it decoded one Updatr
    /// knows, that meaning wins over the bare number: "installer exit 6: a running application is
    /// using the package", not "installer: 0x00000006".
    /// </summary>
    private static PackageOutcome InstallerOutcome(long exitCode, long extended, string what)
    {
        Core.Errors.ErrorCodeInfo interpreted = Core.Errors.ErrorCodes.Describe(extended);

        if (extended != 0 && interpreted.Hint is not null)
            return PackageOutcome.FromCode(extended) with { Detail = $"{what} exit {exitCode}: {interpreted.Hint}" };

        PackageOutcome raw = PackageOutcome.FromCode(exitCode);
        return raw with { Detail = $"{what}: {raw.Detail}" };
    }

    /// <summary>
    /// winget's installed-scope metadata. Phase 7 finding: per-machine installs report "System",
    /// not "Machine" (the manifest's word) - so read both.
    /// </summary>
    internal static InstallScope ParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "machine" or "system" => InstallScope.Machine,
        "user" => InstallScope.User,
        _ => InstallScope.Unknown,
    };

    /// <summary>An Uninstall entry for this product code under HKLM (64- or 32-bit view).</summary>
    private static bool MachineUninstallEntryExists(string productCode)
    {
        if (productCode.Length == 0 || productCode.IndexOfAny(['\\', '/']) >= 0)
            return false;

        foreach (Microsoft.Win32.RegistryView view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using Microsoft.Win32.RegistryKey root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using Microsoft.Win32.RegistryKey? entry = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{productCode}");

            if (entry is not null)
                return true;
        }

        return false;
    }

    /// <summary>Drops the cached lookup connection: it may hold a snapshot of the installed state from before a change.</summary>
    private void ForgetLookupConnection()
    {
        _lookupGate.Wait();

        try
        {
            _lookupCatalog = null;
        }
        finally
        {
            _lookupGate.Release();
        }
    }

    private async Task<PackageOutcome> RunAsync(InstalledPackage package, bool reinstall, IOperationSink sink, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(package, reinstall, sink, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ForgetLookupConnection();
        }
    }

    private async Task<PackageOutcome> RunCoreAsync(InstalledPackage package, bool reinstall, IOperationSink sink, CancellationToken cancellationToken)
    {
        string itemId = package.Key.ToString();

        if (elevation is not null && (package.RunsElevated || package.Scope == InstallScope.Machine))
        {
            BrokerClient? broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

            if (broker is null)
            {
                DiagLog.PackageRoute(package.Id, elevated: false, "administrator access was declined");
                sink.Report(new LogLine(LogLevel.Warning, $"{package.Name}: administrator access was declined; the installer may ask for it itself", itemId));
            }
            else if (package.Scope == InstallScope.User && !await elevation.BrokerIsSameUserAsync(broker).ConfigureAwait(false))
            {
                // A per-user app under another account's elevation would install into that account.
                DiagLog.PackageRoute(package.Id, elevated: false, "per-user install and the elevated helper is a different user");
            }
            else
            {
                DiagLog.PackageRoute(package.Id, elevated: true, package.Scope == InstallScope.Machine ? "machine-wide install" : "its installer needs administrator access");
                PackageOutcome elevated = await RunInBrokerAsync(broker, package, reinstall, sink, cancellationToken).ConfigureAwait(false);

                if (elevated.Code?.Code != ProhibitsElevation)
                    return elevated;

                DiagLog.PackageRoute(package.Id, elevated: false, "the installer refuses to run elevated");
                sink.Report(new LogLine(LogLevel.Info, $"{package.Name}: the installer refuses to run elevated; running it normally", itemId));
            }
        }
        else
        {
            DiagLog.PackageRoute(package.Id, elevated: false, "per-user install that does not need administrator access");
        }

        return await Task.Run(() => RunLocalAsync(package.Id, reinstall, package.HasUnknownVersion, itemId, sink), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a package that is not installed yet, from the named catalog (winget, msstore).
    /// Machine-wide or elevation-needing installers go through the broker, like upgrades.
    /// </summary>
    public async Task<PackageOutcome> InstallNewAsync(string id, string catalogName, string itemId, IOperationSink sink)
    {
        try
        {
            return await InstallNewCoreAsync(id, catalogName, itemId, sink).ConfigureAwait(false);
        }
        finally
        {
            ForgetLookupConnection();
        }
    }

    private async Task<PackageOutcome> InstallNewCoreAsync(string id, string catalogName, string itemId, IOperationSink sink)
    {
        if (elevation is not null && await Task.Run(() => NewInstallNeedsElevation(id, catalogName)).ConfigureAwait(false))
        {
            BrokerClient? broker = await elevation.GetBrokerAsync(CancellationToken.None).ConfigureAwait(false);

            if (broker is not null)
            {
                JsonObject request = new() { ["id"] = id, ["mode"] = "install", ["catalog"] = catalogName };

                try
                {
                    PackageOutcome elevated = BrokerOutcome.FromJson(await broker.SendAsync(BrokerRunType, request, e => sink.Report(BrokerProgress.FromJson(e, itemId)), CancellationToken.None).ConfigureAwait(false));

                    if (elevated.Code?.Code != ProhibitsElevation)
                        return elevated;
                }
                catch (BrokerException ex)
                {
                    return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
                }
            }
        }

        return await Task.Run(() => RunInstallLocalAsync(id, catalogName, itemId, sink)).ConfigureAwait(false);
    }

    /// <summary>Installs from a named catalog in this process. Also what the broker runs for "install".</summary>
    public static async Task<PackageOutcome> RunInstallLocalAsync(string id, string catalogName, string itemId, IOperationSink sink)
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

        CatalogPackage? package = FindInCatalog(manager, id, catalogName);

        if (package is null)
            return PackageOutcome.Failed($"winget's {catalogName} source has no package '{id}'");

        InstallOptions options = new() { PackageInstallMode = PackageInstallMode.Silent, AcceptPackageAgreements = true };
        var operation = manager.InstallPackageAsync(package, options);
        operation.Progress = (_, progress) => sink.Report(ToProgress(progress, itemId));

        return Map(await operation.AsTask().ConfigureAwait(false));
    }

    private static CatalogPackage? FindInCatalog(PackageManager manager, string id, string catalogName)
    {
        ConnectResult connect = manager.GetPackageCatalogByName(catalogName).Connect();

        if (connect.Status != ConnectResultStatus.Ok)
            throw new PackageSourceException($"winget could not open its {catalogName} source ({connect.Status})");

        FindPackagesOptions options = new();
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
        FindPackagesResult result = connect.PackageCatalog.FindPackages(options);

        return result.Status == FindPackagesResultStatus.Ok && result.Matches.Count > 0 ? result.Matches[0].CatalogPackage : null;
    }

    private static bool NewInstallNeedsElevation(string id, string catalogName)
    {
        try
        {
            CatalogPackage? package = FindInCatalog(WinGetRuntime.CreatePackageManager(), id, catalogName);
            return package is not null && NeedsElevation(string.Empty, package.DefaultInstallVersion);
        }
        catch (Exception ex) when (ex is PackageSourceException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return false; // The install itself will report the real problem.
        }
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

    private async Task ProduceAsync(ChannelWriter<InstalledPackage> output, int workers, IOperationSink sink, CancellationToken cancellationToken)
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

            // A fixed set of workers, each holding one catalog connection for the whole listing,
            // pulling groups off a shared queue. In-flight queries always finish: cancellation is
            // only checked between groups.
            System.Collections.Concurrent.ConcurrentQueue<IGrouping<string, LocalEntry>> queue = new(groups);

            async Task WorkAsync()
            {
                PackageCatalog catalog = ConnectComposite(WinGetRuntime.CreatePackageManager());

                while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out IGrouping<string, LocalEntry>? group))
                {
                    if (!await TryQueryAsync(catalog, group.Key, Emit).ConfigureAwait(false))
                    {
                        // Split the failing group by its first two letters and try each part.
                        foreach (IGrouping<string, LocalEntry> part in group.GroupBy(l => Prefix(l.Name, 2)))
                        {
                            if (await TryQueryAsync(catalog, part.Key, Emit).ConfigureAwait(false))
                                continue;

                            DiagLog.WinGetPartitionFailed(part.Key, part.Count());

                            foreach (LocalEntry entry in part)
                                await Emit(entry.ToUncheckedPackage()).ConfigureAwait(false);
                        }
                    }

                    int completed = Interlocked.Increment(ref done);
                    sink.Report(new ProgressReported(null, (double)completed / groups.Count, Message: $"winget: checked {completed} of {groups.Count} groups"));
                }
            }

            await Task.WhenAll(Enumerable.Range(0, Math.Clamp(workers, 1, Math.Max(1, groups.Count))).Select(_ => Task.Run(WorkAsync, CancellationToken.None))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            output.Complete();
        }
        catch (Exception ex)
        {
            output.Complete(ex);
        }
    }

    private static async Task<bool> TryQueryAsync(PackageCatalog catalog, string prefix, Func<InstalledPackage, ValueTask> emit)
    {
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
        => FindCatalogPackage(ConnectComposite(manager), id, out status);

    private static CatalogPackage? FindCatalogPackage(PackageCatalog catalog, string id, out FindPackagesResultStatus status)
    {
        FindPackagesOptions options = new();
        options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
        FindPackagesResult result = catalog.FindPackages(options);
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
            package.Id,
            installed?.Publisher,
            package.Name);

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
            Scope = ParseScope(scope),
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
        InstallScope parsed = ParseScope(installedScope);

        if (parsed == InstallScope.Machine)
            return true;

        if (offered is null)
            return false;

        try
        {
            PackageInstallerInfo? installer = offered.GetApplicableInstaller(new InstallOptions());

            if (installer is null)
                return false;

            // Per-user by registration, but the installer asks for admin anyway (Python: its parts
            // and launcher are machine-wide). Run it elevated so the session's one prompt covers it;
            // RunAsync only does so when the elevated helper is this same user.
            if (parsed == InstallScope.User)
                return installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf;

            return installer.Scope == PackageInstallerScope.System
                || installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf;
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
            InstallResultStatus.InstallError when result.InstallerErrorCode != 0 => InstallerOutcome(result.InstallerErrorCode, extended, "installer"),
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

/// <summary>An installed package as winget's local catalog describes it.</summary>
/// <param name="ProductCodes">MSI product codes, or the Uninstall subkey names for other installers.</param>
public sealed record InstalledDetails(string Name, string? Publisher, string? InstallLocation, string? UninstallCommand, IReadOnlyList<string> ProductCodes);
