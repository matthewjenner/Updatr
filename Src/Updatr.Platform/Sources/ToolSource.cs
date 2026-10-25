using System.Runtime.CompilerServices;
using Updatr.Core.Abstractions;
using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Platform.Sources;

/// <summary>
/// Base for sources driven by an ecosystem's own CLI (npm, pipx, dotnet, code). When the tool is
/// not installed the source simply lists nothing - its packages appear once a cart installs it.
/// </summary>
public abstract class ToolSource(IProcessRunner processes, string program) : IPackageSource, IAppInstaller
{
    protected static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(2);
    protected static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Bounded parallelism for registry lookups ("what is the latest version").</summary>
    protected const int LookupParallelism = 8;

    protected IProcessRunner Processes { get; } = processes;

    public abstract string Id { get; }

    /// <summary>What the Updates screen shows in its Source column.</summary>
    protected abstract string DisplayName { get; }

    /// <summary>
    /// The tool's full path, or null when it is not installed. Looked up every time (a PATH scan
    /// is cheap): a cart may install the tool mid-run and refresh PATH.
    /// </summary>
    protected string? ToolPath => Processes.FindOnPath(program);

    public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (ToolPath is null)
            yield break;

        IReadOnlyList<InstalledPackage> packages;

        try
        {
            packages = await ListAsync(sink, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // One ecosystem failing must not blank the screen; say so and list nothing for it.
            sink.Report(new LogLine(LogLevel.Warning, $"{DisplayName}: could not list packages ({ex.Message})"));
            yield break;
        }

        foreach (InstalledPackage package in packages)
            yield return package;
    }

    public async Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
    {
        if (ToolPath is null)
            return null;

        IReadOnlyList<InstalledPackage> all = await ListAsync(NullSink.Instance, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunActionAsync(package, UpgradeArguments(package), sink, package.Key.ToString());

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunActionAsync(package, ReinstallArguments(package), sink, package.Key.ToString());

    public Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => RunActionAsync(package, UninstallArguments(package), sink, package.Key.ToString());

    /// <summary>Lists installed packages with their available updates.</summary>
    protected abstract Task<IReadOnlyList<InstalledPackage>> ListAsync(IOperationSink sink, CancellationToken cancellationToken);

    protected abstract IReadOnlyList<string> UpgradeArguments(InstalledPackage package);

    protected abstract IReadOnlyList<string> ReinstallArguments(InstalledPackage package);

    protected abstract IReadOnlyList<string> UninstallArguments(InstalledPackage package);

    /// <summary>The package name a catalog source names, when the source is this ecosystem's; else null.</summary>
    protected abstract string? PackageNameOf(PackageSourceRef source);

    /// <summary>Arguments that install <paramref name="name"/> fresh.</summary>
    protected abstract IReadOnlyList<string> InstallArguments(string name);

    public bool Handles(PackageSourceRef source) => PackageNameOf(source) is not null;

    public async Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken)
        => PackageNameOf(source) is { } name && await FindInstalledAsync(name, cancellationToken).ConfigureAwait(false) is not null;

    public Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        string name = PackageNameOf(source) ?? throw new ArgumentException($"{program} does not install {source.SourceId} packages.");
        InstalledPackage target = new() { Source = Id, Id = name, Name = name, InstalledVersion = string.Empty };
        return RunActionAsync(target, InstallArguments(name), sink, itemId);
    }

    /// <summary>Runs the tool for listing; returns stdout. Exit codes are the caller's business.</summary>
    protected async Task<ProcessResult> RunToolAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await Processes.RunAsync(ToolPath ?? throw new InvalidOperationException($"{program} is not installed"), arguments, ListTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Builds an installed-package row for this source.</summary>
    protected InstalledPackage Package(string name, string installed, string? latest, string? problem = null) => new()
    {
        Source = Id,
        Id = name,
        Name = name,
        InstalledVersion = installed,
        AvailableVersion = VersionComparer.IsNewer(latest, installed) ? latest : null,
        Catalog = DisplayName,
        Scope = InstallScope.User,
        CheckProblem = problem,
    };

    private async Task<PackageOutcome> RunActionAsync(InstalledPackage package, IReadOnlyList<string> arguments, IOperationSink sink, string itemId)
    {
        if (ToolPath is null)
            return PackageOutcome.Failed($"{program} is not installed");

        // npm, pipx, and code are .cmd files: cmd.exe re-parses their arguments, so only names and
        // versions that cannot carry shell syntax are ever passed (EcosystemParsers.IsSafePackageName).
        if (!EcosystemParsers.IsSafePackageName(package.Id) || (package.InstalledVersion.Length > 0 && !EcosystemParsers.IsSafeVersion(package.InstalledVersion)))
            return PackageOutcome.Failed($"Refusing to pass '{package.Id}' to {program}: unexpected characters in the name or version");

        sink.Report(new ProgressReported(itemId, null, Message: $"Running {program}"));

        try
        {
            ProcessResult result = await Processes.RunAsync(ToolPath, arguments, InstallTimeout, CancellationToken.None).ConfigureAwait(false);

            if (result.ExitCode == 0)
                return PackageOutcome.Ok();

            string last = (result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? string.Empty;

            return PackageOutcome.Failed($"{program} exited {result.ExitCode}: {last}");
        }
        catch (TimeoutException ex)
        {
            return PackageOutcome.Failed(ex.Message);
        }
    }

    /// <summary>Runs <paramref name="lookup"/> for each name, bounded, keeping failures as nulls.</summary>
    protected static async Task<IReadOnlyDictionary<string, string?>> LookupAllAsync(
        IEnumerable<string> names, Func<string, CancellationToken, Task<string?>> lookup, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> latest = new(StringComparer.OrdinalIgnoreCase);
        Lock gate = new();

        await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = LookupParallelism, CancellationToken = cancellationToken }, async (name, ct) =>
        {
            string? version;

            try
            {
                version = await lookup(name, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or System.Xml.XmlException)
            {
                version = null;
            }

            lock (gate)
                latest[name] = version;
        }).ConfigureAwait(false);

        return latest;
    }

    /// <summary>A sink that drops everything (internal lookups nobody watches).</summary>
    protected sealed class NullSink : IOperationSink
    {
        public static readonly NullSink Instance = new();

        public void Report(OperationEvent operationEvent)
        {
        }

        public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
