using System.Diagnostics;
using System.Text;
using Updatr.Core.Abstractions;

namespace Updatr.Platform.Processes;

/// <summary>Starts external programs for Updatr. See <see cref="IProcessRunner"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        // Ecosystem CLIs draw progress bars and colors when they think a person is watching.
        info.Environment["NO_COLOR"] = "1";
        info.Environment["WINGET_DISABLE_PROGRESS"] = "1";

        using Process process = new() { StartInfo = info };
        process.Start();

        // Nothing is ever typed into these; a closed stdin makes "press any key" prompts fail fast.
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the timeout and the kill.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0} s");
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public string? FindOnPath(string name)
    {
        string[] extensions = Path.HasExtension(name)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate;

                try
                {
                    candidate = Path.Combine(directory.Trim().Trim('"'), name + extension);
                }
                catch (ArgumentException)
                {
                    continue; // A malformed PATH entry.
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
