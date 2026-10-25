namespace Updatr.Core.Sources;

/// <summary>A running program that keeps an app's files in use, so its installer cannot replace them.</summary>
/// <param name="Name">Program file name ("ollama.exe").</param>
/// <param name="Note">Why it is not obvious, when it is not ("no window - it hosts fbw-simbridge.exe (pid 9)").</param>
public sealed record BlockingProcess(string Name, int ProcessId, string? Note = null)
{
    public override string ToString() => Note is null ? $"{Name} (pid {ProcessId})" : $"{Name} (pid {ProcessId}; {Note})";

    /// <summary>
    /// "a.exe (pid 1), b.exe (19 processes) and 3 more" - short enough for one status line. A
    /// program with many processes (browsers, VS Code) is one entry.
    /// </summary>
    public static string Describe(IReadOnlyList<BlockingProcess> processes, int shown = 3)
    {
        string[] entries = [.. processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First().ToString() : $"{g.Key} ({g.Count()} processes)")];

        string list = string.Join(", ", entries.Take(shown));
        return entries.Length > shown ? $"{list} and {entries.Length - shown} more" : list;
    }
}

/// <summary>
/// Finds the programs that keep a package's files in use: the ones an installer reports as "in
/// use", or that keep an update from applying. Platform implementation; read-only - it never
/// closes anything.
/// </summary>
public interface IInUseFinder
{
    Task<IReadOnlyList<BlockingProcess>> FindAsync(InstalledPackage package, CancellationToken cancellationToken);
}
