using System.Runtime.CompilerServices;
using Updatr.Core.Abstractions;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Platform.WinGet;

/// <summary>
/// winget through <c>winget.exe</c> and its text output. Fallback only, for when the COM API cannot
/// be activated but the CLI works. Limited: it lists only packages with updates, and the table is
/// English-only (see <see cref="WinGetTableParser"/>).
/// </summary>
public sealed class WinGetCliSource(IProcessRunner processes, string wingetPath) : IPackageSource
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(60);

    private static readonly string[] Common = ["--accept-source-agreements", "--disable-interactivity"];

    public string Id => WinGetComSource.SourceId;

    public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sink.Report(new LogLine(LogLevel.Warning, "winget's API is unavailable; using its command line, which lists only packages with updates"));
        ProcessResult result = await processes.RunAsync(wingetPath, ["upgrade", "--include-unknown", .. Common], ListTimeout, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WinGetTableParser.Row>? rows = WinGetTableParser.Parse(result.StandardOutput);

        if (rows is null)
            throw new PackageSourceException($"Could not read winget's output (exit {result.ExitCode}). Its table is parsed only in English.");

        foreach (WinGetTableParser.Row row in rows)
        {
            yield return new InstalledPackage
            {
                Source = Id,
                Id = row.Id,
                Name = row.Name,
                InstalledVersion = row.Version,
                AvailableVersion = row.Available.Length > 0 ? row.Available : null,
                Catalog = row.Source.Length > 0 ? row.Source : null,
                IsManaged = !row.IdTruncated,
                CheckProblem = row.IdTruncated ? "winget truncated this id; it cannot be targeted exactly" : null,
            };
        }
    }

    public async Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
    {
        ProcessResult result = await processes.RunAsync(wingetPath, ["list", "--id", id, "--exact", .. Common], ListTimeout, cancellationToken).ConfigureAwait(false);
        WinGetTableParser.Row? row = WinGetTableParser.Parse(result.StandardOutput)?.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        return row is null ? null : new InstalledPackage
        {
            Source = Id,
            Id = row.Id,
            Name = row.Name,
            InstalledVersion = row.Version,
            AvailableVersion = row.Available.Length > 0 ? row.Available : null,
            Catalog = row.Source.Length > 0 ? row.Source : null,
        };
    }

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(["upgrade", "--id", package.Id, "--exact", "--silent", "--accept-package-agreements", .. Common], package, sink);

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(["install", "--id", package.Id, "--exact", "--force", "--silent", "--accept-package-agreements", .. Common], package, sink);

    public Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunAsync(["uninstall", "--id", package.Id, "--exact", "--silent", .. Common], package, sink);

    private async Task<PackageOutcome> RunAsync(string[] arguments, InstalledPackage package, IOperationSink sink)
    {
        sink.Report(new ProgressReported(package.Key.ToString(), null, Message: "Running winget"));

        try
        {
            ProcessResult result = await processes.RunAsync(wingetPath, arguments, InstallTimeout, CancellationToken.None).ConfigureAwait(false);
            PackageOutcome outcome = PackageOutcome.FromCode(result.ExitCode);
            return outcome.Kind == OutcomeKind.Ok ? outcome : outcome with { Detail = "exit " + outcome.Detail };
        }
        catch (TimeoutException ex)
        {
            return PackageOutcome.Failed(ex.Message);
        }
    }
}
