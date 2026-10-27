using System.Runtime.InteropServices;
using Updatr.Core;
using Updatr.Core.Abstractions;
using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Leftovers;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Core.Diagnostics;
using Updatr.Core.Preflight;
using Updatr.Core.Settings;
using Updatr.Platform.Catalog;
using Updatr.Platform.Data;
using Updatr.Platform.Debloat;
using Updatr.Platform.Leftovers;
using Updatr.Platform.DirectUrl;
using Updatr.Platform.Elevation;
using Updatr.Platform.Net;
using Updatr.Platform.Preflight;
using Updatr.Platform.Processes;
using Updatr.Platform.Sources;
using Updatr.Platform.Update;
using Updatr.Platform.Windows;
using Updatr.Platform.WinGet;

namespace Updatr.Platform;

/// <summary>Start-up options shared by both front ends.</summary>
/// <param name="FrontEnd">"gui" or "cli" - used in log file names.</param>
/// <param name="DataRoot">Data folder override (<c>--data-dir</c>); null for <c>Updatr.data\</c> beside the exe.</param>
public sealed record HostOptions(string FrontEnd, string? DataRoot = null);

/// <summary>
/// Composition root. The GUI and the CLI build exactly this, so they cannot drift in how anything
/// is wired - only in how they present it.
/// </summary>
public sealed class UpdatrHost : IAsyncDisposable
{
    private readonly RunLogWriter _log;

    private UpdatrHost(HostOptions options)
    {
        Data = new PortableDataStore(options.DataRoot ?? AppLocations.DefaultDataRoot);
        Data.Failed += DiagLog.DataFailure;

        DiagLog.Started(options.FrontEnd, ProductInfo.Version, WindowsInfo.Os().ToString(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), WindowsInfo.IsElevated);
        DiagLog.DataFolder(Data.DisplayRoot, AppLocations.FileSystemOf(Data.DisplayRoot));

        _log = new RunLogWriter(Data, options.FrontEnd);
        Processes = new ProcessRunner();
        Http = UpdatrHttp.Client;
        CatalogCache = new DataStoreCatalogCache(Data);
        Elevation = new ElevationService(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running exe."), Data.DisplayRoot);
        Preflight = new RunPreflight(CreateChecks());
        TestElevation = new TestElevation(Elevation);

        Holds = new HoldStore(Data);
        SettingsStore settings = new(Data);
        SettingsShow = new ShowSettings(settings);
        SettingsSet = new ChangeSettings(settings);
        WinGet = new WinGetSource(Elevation, Processes);
        NpmSource npm = new(Processes);
        PipxSource pipx = new(Processes, Http);
        DotnetToolSource dotnetTools = new(Processes, Http);
        PsGallerySource gallery = new(Http, Elevation);
        VsCodeExtensionSource vscode = new(Processes, Http);
        Sources = [WinGet, npm, pipx, dotnetTools, gallery, vscode];
        List = new ListPackages(Sources, HoldPolicyAsync);

        // Phase 8: leftovers after uninstall, quarantine, install footprints - all per machine.
        string machine = MachineIdentity.Id;
        Footprints footprints = new(Data, machine);
        LeftoverStore leftoverStore = new(Data, machine);
        LeftoverScanner scanner = new(Data.DisplayRoot);
        Quarantine quarantine = new(Data.DisplayRoot, machine, Elevation);
        LeftoverService leftovers = new(scanner, leftoverStore, footprints, quarantine);
        LeftoversList = new ListLeftovers(leftoverStore, scanner);
        LeftoversQuarantine = new QuarantineLeftovers(leftoverStore, quarantine);
        LeftoversDismiss = new DismissLeftovers(leftoverStore);
        QuarantineList = new ListQuarantine(quarantine);
        QuarantineRestore = new RestoreQuarantine(quarantine);
        QuarantinePurge = new PurgeQuarantine(quarantine);

        PackageBatch batch = new(Sources, List, new InstallerGate(), HoldPolicyAsync, leftovers, inUse: new InUseFinder(scanner));
        Upgrade = new UpgradePackages(batch);
        Reinstall = new ReinstallPackages(batch);
        Uninstall = new UninstallPackages(batch);
        FreshInstall = new FreshInstallPackages(batch);
        Hold = new HoldPackages(Holds);
        Unhold = new UnholdPackages(Holds, async ct => (await GetCatalogAsync(ct).ConfigureAwait(false)).Catalog.Holds);
        RepairWinGet = new RepairWinGet(Http, WinGet);
        SelfUpdate = new SelfUpdater(Http);

        // Phase 5: carts, templates, installs.
        Func<CancellationToken, Task<AppCatalog>> catalog = async ct => (await GetCatalogAsync(ct).ConfigureAwait(false)).Catalog;
        Carts = new CartStore(Data);
        Templates = new TemplateCatalog(catalog, Carts);
        Tools = new ToolEnvironment(Processes);
        IReadOnlyList<IAppInstaller> installers = Installers =
        [
            new WinGetAppInstaller(WinGet, Processes),
            npm,
            pipx,
            dotnetTools,
            vscode,
            new PsGalleryAppInstaller(Http, gallery),
            new DirectUrlAppInstaller(new DirectUrlInstaller(Http, Processes, Elevation)),
        ];
        Install = new InstallApps(catalog, Templates, Carts, installers, new InstallerGate(), Tools, footprints);
        CatalogList = new ListCatalog(catalog);
        CartList = new ListCarts(Carts);
        CartShow = new ShowCart(Carts);
        CartSave = new SaveCart(Templates, Carts);
        CartRemove = new RemoveFromCart(Carts);
        CartDelete = new DeleteCart(Carts);
        TemplateList = new ListTemplates(Templates);
        TemplateShow = new ShowTemplate(Templates);
        TemplateSave = new SaveTemplate(catalog, Carts);
        TemplateCopy = new CopyTemplate(Templates, Carts);
        TemplateDelete = new DeleteTemplate(catalog, Carts);

        // Phase 6: debloat.
        AppxTarget appx = new(Elevation, WinGet, Processes);
        IDebloatTarget[] targets = [new RegistryTarget(Elevation), appx, new ServiceTarget(Elevation), new ScheduledTaskTarget(Elevation)];
        DebloatStore debloatStore = new(Data);
        DebloatEngine debloat = new(catalog, targets, appx, new RestorePoint(Elevation), debloatStore, WindowsInfo.Os().Build);
        DebloatStatus = new DebloatStatus(debloat);
        DebloatApply = new ApplyDebloat(debloat);
        DebloatRevert = new RevertDebloat(debloat);
        DebloatHistory = new DebloatHistory(debloatStore);
        DebloatKeep = new KeepApps(debloatStore);
        DebloatUnkeep = new UnkeepApps(debloatStore);
    }

    public ListLeftovers LeftoversList { get; }

    public QuarantineLeftovers LeftoversQuarantine { get; }

    public DismissLeftovers LeftoversDismiss { get; }

    public ListQuarantine QuarantineList { get; }

    public RestoreQuarantine QuarantineRestore { get; }

    public PurgeQuarantine QuarantinePurge { get; }

    public DebloatStatus DebloatStatus { get; }

    public ApplyDebloat DebloatApply { get; }

    public RevertDebloat DebloatRevert { get; }

    public DebloatHistory DebloatHistory { get; }

    public KeepApps DebloatKeep { get; }

    public UnkeepApps DebloatUnkeep { get; }

    public CartStore Carts { get; }

    public IReadOnlyList<IAppInstaller> Installers { get; }

    /// <summary>Whether a catalog app is installed, per its first source the installers handle. False when unknown.</summary>
    public async Task<bool> IsInstalledAsync(CatalogApp app, CancellationToken cancellationToken)
    {
        PackageSourceRef source = app.Sources[0];
        IAppInstaller? installer = Installers.FirstOrDefault(i => i.Handles(source));

        try
        {
            return installer is not null && await installer.IsInstalledAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public TemplateCatalog Templates { get; }

    public ToolEnvironment Tools { get; }

    public InstallApps Install { get; }

    public ListCatalog CatalogList { get; }

    public ListCarts CartList { get; }

    public ShowCart CartShow { get; }

    public SaveCart CartSave { get; }

    public RemoveFromCart CartRemove { get; }

    public DeleteCart CartDelete { get; }

    public ListTemplates TemplateList { get; }

    public ShowTemplate TemplateShow { get; }

    public SaveTemplate TemplateSave { get; }

    public CopyTemplate TemplateCopy { get; }

    public DeleteTemplate TemplateDelete { get; }

    public HoldStore Holds { get; }

    public WinGetSource WinGet { get; }

    public IReadOnlyList<IPackageSource> Sources { get; }

    public ListPackages List { get; }

    public UpgradePackages Upgrade { get; }

    public ReinstallPackages Reinstall { get; }

    public UninstallPackages Uninstall { get; }

    public HoldPackages Hold { get; }

    public FreshInstallPackages FreshInstall { get; }

    public ShowSettings SettingsShow { get; }

    public ChangeSettings SettingsSet { get; }

    public UnholdPackages Unhold { get; }

    public RepairWinGet RepairWinGet { get; }

    public SelfUpdater SelfUpdate { get; }

    private CatalogLoadResult? _catalog;

    /// <summary>Uses the catalog preflight already loaded, so it is not fetched twice.</summary>
    public void UseCatalog(CatalogLoadResult? catalog)
    {
        if (catalog is not null)
            _catalog = catalog;
    }

    /// <summary>The catalog: the one preflight loaded, else loaded now (GitHub, then the drive, then built in).</summary>
    public async Task<CatalogLoadResult> GetCatalogAsync(CancellationToken cancellationToken)
        => _catalog ??= await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Fetches the catalog again - the GUI's periodic check, so catalog fixes reach a long-running
    /// session. The current one stays in use until the new one has loaded.
    /// </summary>
    public async Task<CatalogLoadResult> ReloadCatalogAsync(CancellationToken cancellationToken)
        => _catalog = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The newer Updatr version published on GitHub, or null (current, offline, or GitHub unreachable).</summary>
    public async Task<string?> FindNewerVersionAsync(CancellationToken cancellationToken)
    {
        PreflightContext context = new() { IsOnline = true };
        await new SelfUpdateCheck(Http).RunAsync(context, cancellationToken).ConfigureAwait(false);
        return context.NewerVersion;
    }

    private Task<CatalogLoadResult> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        List<ICatalogOrigin> origins = [new RemoteCatalogOrigin(Http), CatalogCache, new EmbeddedCatalogOrigin()];

        if (FolderCatalogOrigin.FromEnvironment() is { } folder)
        {
            DiagLog.CatalogOverride(folder.Folder);
            origins.Insert(0, folder);
        }

        return new CatalogLoader(origins, CatalogCache).LoadAsync(cancellationToken);
    }

    /// <summary>Catalog holds plus the user's own, read fresh so a hold made a moment ago applies.</summary>
    public async Task<HoldPolicy> HoldPolicyAsync(CancellationToken cancellationToken)
    {
        CatalogLoadResult catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        UserHoldsFile user = await Holds.LoadAsync(cancellationToken).ConfigureAwait(false);
        return HoldStore.PolicyFor(catalog.Catalog.Holds, user);
    }

    public IDataStore Data { get; }

    public IProcessRunner Processes { get; }

    public HttpClient Http { get; }

    public DataStoreCatalogCache CatalogCache { get; }

    public ElevationService Elevation { get; }

    public RunPreflight Preflight { get; }

    public TestElevation TestElevation { get; }

    /// <summary>This run's log file, relative to the data folder.</summary>
    public string LogFile => _log.RelativePath;

    public static async Task<UpdatrHost> StartAsync(HostOptions options, CancellationToken cancellationToken)
    {
        UpdatrHost host = new(options);
        SelfUpdater.CleanUpOld(AppLocations.ExeDirectory);
        await RunLogWriter.PruneAsync(host.Data, cancellationToken).ConfigureAwait(false);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await Elevation.DisposeAsync().ConfigureAwait(false);
        await _log.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The preflight checks, in the order the splash and the CLI list them.</summary>
    private List<IPreflightCheck> CreateChecks() =>
    [
        new OsCheck(),
        new ArchitectureCheck(),
        new NetworkCheck(Http),
        new WinGetCheck(),
        new CatalogCheck(new RemoteCatalogOrigin(Http), CatalogCache),
        new ToolsCheck(Processes),
        new ElevationCheck(),
        new PendingRebootCheck(),
        new InstallerActivityCheck(),
        new DiskSpaceCheck(),
        new DataFolderCheck(Data),
        new SelfUpdateCheck(Http),
    ];
}
