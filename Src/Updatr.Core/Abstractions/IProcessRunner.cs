namespace Updatr.Core.Abstractions;

/// <summary>
/// Runs an external program and captures its output. The only way Updatr starts a process
/// (ecosystem CLIs, installers), so timeouts, encoding, and logging are handled in one place.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with arguments passed individually (never a joined command
    /// line), waits for it to exit, and returns its output.
    /// </summary>
    /// <param name="fileName">Program to run - a full path, or a name resolved on PATH.</param>
    /// <param name="arguments">Arguments, each escaped by the runner.</param>
    /// <param name="timeout">Kill the process tree and throw <see cref="TimeoutException"/> after this long.</param>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Finds a program on PATH (honoring PATHEXT) without running it. Returns the full path, or
    /// null when it is not installed.
    /// </summary>
    string? FindOnPath(string name);
}

/// <summary>What a finished process produced.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
