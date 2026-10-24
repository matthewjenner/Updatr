using Updatr.Core.Catalog;
using Updatr.Core.Operations;

namespace Updatr.Core.Preflight;

/// <summary>How a check came out. Ordered by severity.</summary>
public enum PreflightStatus
{
    /// <summary>Informational - nothing to act on (e.g. "Windows 11 Pro 24H2").</summary>
    Info,

    /// <summary>All good.</summary>
    Pass,

    /// <summary>Usable, with a limitation the user should know about.</summary>
    Warn,

    /// <summary>
    /// Something Updatr needs is missing. The splash stays open and offers Retry / Continue anyway;
    /// the CLI exits non-zero.
    /// </summary>
    Fail,
}

/// <summary>
/// An action that can fix a failed check. The GUI shows a button on the row; the CLI prints the
/// command. Both run the same operation.
/// </summary>
/// <param name="Id">Stable id the front ends map to an operation (e.g. "winget-repair").</param>
/// <param name="Label">Button text ("Install winget").</param>
/// <param name="CliCommand">The equivalent command line ("updatr-cli winget repair").</param>
public sealed record PreflightFix(string Id, string Label, string CliCommand);

/// <summary>Outcome of one check.</summary>
/// <param name="Status">How it came out.</param>
/// <param name="Summary">One line for the splash row and CLI output.</param>
/// <param name="Detail">Longer explanation and what to do, shown on expand / with <c>--verbose</c>.</param>
/// <param name="Fix">An action that can resolve it, if Updatr has one.</param>
public sealed record PreflightResult(PreflightStatus Status, string Summary, string? Detail = null, PreflightFix? Fix = null)
{
    public static PreflightResult Pass(string summary, string? detail = null) => new(PreflightStatus.Pass, summary, detail);

    public static PreflightResult Info(string summary, string? detail = null) => new(PreflightStatus.Info, summary, detail);

    public static PreflightResult Warn(string summary, string? detail = null, PreflightFix? fix = null) => new(PreflightStatus.Warn, summary, detail, fix);

    public static PreflightResult Fail(string summary, string? detail = null, PreflightFix? fix = null) => new(PreflightStatus.Fail, summary, detail, fix);
}

/// <summary>One startup check. Implementations live in Updatr.Platform.</summary>
public interface IPreflightCheck
{
    /// <summary>Stable id, also the CLI's <c>--only</c> value ("network", "winget", ...).</summary>
    string Id { get; }

    /// <summary>Row label on the splash ("Internet connection").</summary>
    string Title { get; }

    /// <summary>Ids of checks whose results this one reads from the context. Run after them.</summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>
    /// Runs the check. Must not throw for an expected condition - return Warn/Fail instead. An
    /// unexpected exception is caught by the runner and reported as Fail.
    /// </summary>
    Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Facts checks learn and later checks (and the app) use. Written by a check, read only by checks
/// that declare it in <see cref="IPreflightCheck.DependsOn"/>, so there are no races.
/// </summary>
public sealed class PreflightContext
{
    /// <summary>Set by the network check: null until it has run.</summary>
    public bool? IsOnline { get; set; }

    /// <summary>Set by the catalog check.</summary>
    public CatalogLoadResult? Catalog { get; set; }

    /// <summary>Set by the winget check: true when the COM API answered.</summary>
    public bool? WinGetAvailable { get; set; }

    /// <summary>Set by the elevation check: true when an elevated broker can be requested.</summary>
    public bool? ElevationAvailable { get; set; }

    /// <summary>Set by the tools check: tool name ("npm") to full path, for tools found on PATH.</summary>
    public IReadOnlyDictionary<string, string> Tools { get; set; } = new Dictionary<string, string>();

    /// <summary>Set by the self-update check: the newer version on GitHub, if any.</summary>
    public string? NewerVersion { get; set; }
}

/// <summary>A check started. Front ends show a spinner on its row.</summary>
public sealed record CheckStarted(string CheckId, string Title) : OperationEvent;

/// <summary>A check finished.</summary>
public sealed record CheckFinished(string CheckId, string Title, PreflightResult Result, TimeSpan Elapsed) : OperationEvent;

/// <summary>What to run.</summary>
/// <param name="Only">Check ids to run; null for all. Dependencies of these run too.</param>
public sealed record PreflightRequest(IReadOnlyCollection<string>? Only = null);

/// <summary>Every check's result, in the order the checks are registered.</summary>
public sealed record PreflightReport(IReadOnlyList<CheckFinished> Results, PreflightContext Context)
{
    public PreflightStatus Worst => Results.Count == 0 ? PreflightStatus.Pass : Results.Max(r => r.Result.Status);

    public bool HasFailures => Worst == PreflightStatus.Fail;
}
