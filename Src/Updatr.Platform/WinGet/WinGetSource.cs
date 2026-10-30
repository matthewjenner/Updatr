using System.Runtime.CompilerServices;
using Updatr.Core.Abstractions;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.WinGet;

/// <summary>
/// The winget source the app uses: the COM API when it works, else <c>winget.exe</c>, else a clear
/// "winget is not available" on every call. Decided on first use, then fixed for the session - or
/// re-decided after <see cref="Reset"/> (after a winget repair).
/// </summary>
public sealed class WinGetSource(ElevationService? elevation, IProcessRunner processes) : IPackageSource
{
    private readonly Lock _gate = new();
    private IPackageSource? _chosen;
    private string? _unavailable;

    public string Id => WinGetComSource.SourceId;

    /// <summary>"COM API", "command line", or "unavailable: reason" - shown in Settings.</summary>
    public string Mode { get; private set; } = "not checked yet";

    public void Reset()
    {
        lock (_gate)
        {
            _chosen = null;
            _unavailable = null;
        }
    }

    public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (InstalledPackage package in Chosen().ListInstalledAsync(sink, cancellationToken).ConfigureAwait(false))
            yield return package;
    }

    public async IAsyncEnumerable<InstalledPackage> ListInstalledQuietlyAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (InstalledPackage package in Chosen().ListInstalledQuietlyAsync(sink, cancellationToken).ConfigureAwait(false))
            yield return package;
    }

    public Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
        => Chosen().FindInstalledAsync(id, cancellationToken);

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => _unavailable is { } reason ? Task.FromResult(PackageOutcome.Failed(reason)) : Chosen().UpgradeAsync(package, sink, cancellationToken);

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => _unavailable is { } reason ? Task.FromResult(PackageOutcome.Failed(reason)) : Chosen().ReinstallAsync(package, sink, cancellationToken);

    public Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => _unavailable is { } reason ? Task.FromResult(PackageOutcome.Failed(reason)) : Chosen().UninstallAsync(package, sink, cancellationToken);

    public bool CanFreshInstall => true;

    /// <summary>Installs the package again from the catalog it came from (winget or msstore), newest version.</summary>
    public Task<PackageOutcome> InstallLatestAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => InstallNewAsync(new Core.Catalog.WingetSourceRef { Id = package.Id, Source = package.Catalog ?? "winget" }, package.Key.ToString(), sink, processes);

    /// <summary>Installs a package that is not installed yet - COM when available, else winget.exe.</summary>
    public async Task<PackageOutcome> InstallNewAsync(Core.Catalog.WingetSourceRef source, string itemId, Core.Operations.IOperationSink sink, IProcessRunner runner)
    {
        IPackageSource chosen;

        try
        {
            chosen = Chosen();
        }
        catch (PackageSourceException ex)
        {
            return PackageOutcome.Failed(ex.Message);
        }

        if (chosen is WinGetComSource com)
            return await com.InstallNewAsync(source.Id, source.Source, itemId, sink).ConfigureAwait(false);

        if (runner.FindOnPath("winget") is not { } winget)
            return PackageOutcome.Failed("winget is not available");

        sink.Report(new Core.Operations.ProgressReported(itemId, null, Message: "Running winget"));
        ProcessResult result = await runner.RunAsync(winget,
            ["install", "--id", source.Id, "--exact", "--source", source.Source, "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(60), CancellationToken.None).ConfigureAwait(false);
        return PackageOutcome.FromCode(result.ExitCode);
    }

    private IPackageSource Chosen()
    {
        lock (_gate)
        {
            if (_chosen is not null)
                return _chosen;

            if (_unavailable is not null)
                throw new PackageSourceException(_unavailable);

            string? problem = WinGetRuntime.EnsureReady();

            if (problem is null)
            {
                try
                {
                    _ = WinGetRuntime.CreatePackageManager().Version;
                    Mode = "COM API";
                    return _chosen = new WinGetComSource(elevation);
                }
                catch (Exception ex) when (WinGetRuntime.IsUnavailable(ex))
                {
                    problem = WinGetRuntime.Describe(ex);
                }
            }

            if (processes.FindOnPath("winget") is { } path)
            {
                Mode = $"command line (API unavailable: {problem})";
                return _chosen = new WinGetCliSource(processes, path);
            }

            _unavailable = $"winget is not available on this machine ({problem}). Run 'updatr-cli winget repair' or use Install winget on the splash.";
            Mode = "unavailable";
            throw new PackageSourceException(_unavailable);
        }
    }
}
