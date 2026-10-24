using System.Globalization;
using Updatr.Core.Operations;

namespace Updatr.Core.Diagnostics;

/// <summary>Area of the app a log entry comes from; the log viewer filters on it.</summary>
public enum LogCategory
{
    System,
    Data,
    Catalog,
    Preflight,
    Elevation,
    Source,
    Queue,
    Debloat,
    Update,
}

/// <summary>One line in the diagnostic log.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogCategory Category, LogLevel Level, string Message)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-7}] {Category,-9} {Message}");
}

/// <summary>
/// Centralized diagnostics with a strongly-typed emit surface (Klakr's pattern): every log site is
/// a method here and the wording lives only here, so messages do not drift. Adding an event means
/// adding a method.
/// </summary>
/// <remarks>
/// Always on: unlike Klakr, every run's log is also written to <c>Updatr.data\logs\</c>, so a
/// failure on a machine you have since left can still be read later from the drive. Listeners are
/// called on the emitting thread; UI listeners marshal themselves.
/// </remarks>
public static class DiagLog
{
    /// <summary>How many entries the in-memory buffer keeps for the log viewer.</summary>
    public const int BufferLimit = 2000;

    private static readonly Lock Gate = new();
    private static readonly LinkedList<LogEntry> Buffer = new();

    /// <summary>Raised for every entry, on the thread that emitted it.</summary>
    public static event Action<LogEntry>? EntryAdded;

    /// <summary>Copy of the buffer, oldest first.</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
            return [.. Buffer];
    }

    // ---- System -------------------------------------------------------------------------

    public static void Started(string frontEnd, string version, string os, string architecture, bool elevated)
        => Emit(LogCategory.System, LogLevel.Info,
            $"Updatr {frontEnd} v{version} started on {os} ({architecture}), elevated={YesNo(elevated)}");

    public static void UnhandledException(string where, Exception ex)
        => Emit(LogCategory.System, LogLevel.Error, $"Unhandled exception in {where}: {ex}");

    // ---- Data ---------------------------------------------------------------------------

    public static void DataFolder(string root, string? fileSystem)
        => Emit(LogCategory.Data, LogLevel.Info, $"Data folder: {root} ({fileSystem ?? "unknown file system"})");

    public static void DataFailure(string message)
        => Emit(LogCategory.Data, LogLevel.Warning, $"Data folder problem: {message}");

    // ---- Catalog ------------------------------------------------------------------------

    public static void CatalogLoaded(string origin, int apps, int templates, int holds, DateTimeOffset? timestamp)
        => Emit(LogCategory.Catalog, LogLevel.Info,
            $"Catalog loaded from {origin}: {apps} apps, {templates} templates, {holds} hold rules"
            + (timestamp is { } t ? string.Create(CultureInfo.InvariantCulture, $" (fetched {t:yyyy-MM-dd HH:mm})") : string.Empty));

    public static void CatalogOverride(string folder)
        => Emit(LogCategory.Catalog, LogLevel.Warning, $"UPDATR_CATALOG_DIR is set: using the catalog in {folder}, not GitHub's");

    public static void CatalogOriginSkipped(string diagnostic)
        => Emit(LogCategory.Catalog, LogLevel.Warning, $"Catalog origin skipped - {diagnostic}");

    // ---- Preflight ----------------------------------------------------------------------

    public static void PreflightCheck(string checkId, string status, string summary, TimeSpan elapsed)
        => Emit(LogCategory.Preflight, status == "Fail" ? LogLevel.Error : status == "Warn" ? LogLevel.Warning : LogLevel.Info,
            string.Create(CultureInfo.InvariantCulture, $"{checkId}: {status} - {summary} ({elapsed.TotalMilliseconds:0} ms)"));

    public static void PreflightCheckCrashed(string checkId, Exception ex)
        => Emit(LogCategory.Preflight, LogLevel.Error, $"{checkId}: check threw {ex.GetType().Name}: {ex.Message}");

    public static void PreflightFinished(int pass, int warn, int fail, TimeSpan elapsed)
        => Emit(LogCategory.Preflight, fail > 0 ? LogLevel.Warning : LogLevel.Info,
            string.Create(CultureInfo.InvariantCulture, $"Preflight done: {pass} passed, {warn} warnings, {fail} failed in {elapsed.TotalSeconds:0.0} s"));

    // ---- Elevation ----------------------------------------------------------------------

    public static void BrokerLaunching(string pipeName)
        => Emit(LogCategory.Elevation, LogLevel.Info, $"Requesting elevation (broker pipe {pipeName})");

    public static void BrokerDeclined()
        => Emit(LogCategory.Elevation, LogLevel.Warning, "Elevation was declined at the UAC prompt; admin-only steps will be skipped");

    public static void BrokerConnected(int brokerProcessId)
        => Emit(LogCategory.Elevation, LogLevel.Info, $"Elevated broker connected (PID {brokerProcessId})");

    public static void BrokerRejectedClient(string reason)
        => Emit(LogCategory.Elevation, LogLevel.Error, $"Broker refused a connection: {reason}");

    public static void BrokerRequest(string type, bool ok, TimeSpan elapsed)
        => Emit(LogCategory.Elevation, ok ? LogLevel.Detail : LogLevel.Warning,
            string.Create(CultureInfo.InvariantCulture, $"Broker request {type}: {(ok ? "ok" : "failed")} ({elapsed.TotalMilliseconds:0} ms)"));

    public static void BrokerExited(string reason)
        => Emit(LogCategory.Elevation, LogLevel.Info, $"Broker exiting: {reason}");

    // ---- Source -------------------------------------------------------------------------

    public static void WinGetMetadataRestored(string path)
        => Emit(LogCategory.Source, LogLevel.Warning, $"WinGet metadata was missing beside the exe; restored {path}");

    public static void WinGetPartitionFailed(string prefix, int packages)
        => Emit(LogCategory.Source, LogLevel.Warning,
            $"winget could not check packages named '{prefix}...' ({packages} package(s)) - a broken entry in its index; listed without update info");

    public static void WinGetRepairStep(string step)
        => Emit(LogCategory.Source, LogLevel.Info, $"winget repair: {step}");

    public static void WinGetMetadataMissing(string reason)
        => Emit(LogCategory.Source, LogLevel.Error, $"WinGet metadata is missing beside the exe and could not be restored: {reason}");

    // ---- Plumbing -----------------------------------------------------------------------

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static void Emit(LogCategory category, LogLevel level, string message)
    {
        LogEntry entry = new(DateTimeOffset.Now, category, level, message);

        lock (Gate)
        {
            Buffer.AddLast(entry);
            if (Buffer.Count > BufferLimit)
                Buffer.RemoveFirst();
        }

        EntryAdded?.Invoke(entry);
    }
}
