using System.Runtime.InteropServices;
using Updatr.Core;
using Updatr.Core.Abstractions;
using Updatr.Core.Diagnostics;
using Updatr.Core.Preflight;
using Updatr.Platform.Catalog;
using Updatr.Platform.Data;
using Updatr.Platform.Elevation;
using Updatr.Platform.Net;
using Updatr.Platform.Preflight;
using Updatr.Platform.Processes;
using Updatr.Platform.Windows;

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
        Elevation = new ElevationService(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running exe."));
        Preflight = new RunPreflight(CreateChecks());
        TestElevation = new TestElevation(Elevation);
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
