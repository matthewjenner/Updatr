using System.Globalization;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;
using Updatr.Platform;
using Updatr.Platform.WinGet;

namespace Updatr.App.ViewModels;

/// <summary>Maps a preflight fix id to the operation that performs it - the same one the CLI runs.</summary>
internal static class FixRunner
{
    /// <summary>
    /// Runs the row's fix off the UI thread and returns a one-line result for the user. While it
    /// runs, the row shows each step and a progress bar (a winget update downloads a few hundred MB).
    /// </summary>
    public static async Task<string> RunAsync(UpdatrHost host, CheckRowViewModel row, CancellationToken cancellationToken = default)
    {
        PreflightFix fix = row.Fix!;
        row.IsFixing = true;
        row.FixProgress = null;
        row.Summary = $"{fix.Label}...";
        Services.GuiSink sink = new(e => Show(row, e));

        try
        {
            return fix.Id switch
            {
                RepairWinGet.FixId => (await Task.Run(() => host.RepairWinGet.RunAsync(true, sink, cancellationToken), cancellationToken)).Summary,
                _ => $"No fix is available here for '{fix.Id}'. Try: {fix.CliCommand}",
            };
        }
        catch (OperationCanceledException)
        {
            return $"{fix.Label} was stopped.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{fix.Label} failed: {ex.Message}";
        }
        finally
        {
            row.IsFixing = false;
            row.FixProgress = null;
        }
    }

    private static void Show(CheckRowViewModel row, OperationEvent e)
    {
        if (!row.IsFixing)
            return; // A late event after the fix finished.

        switch (e)
        {
            case StepStarted step:
                row.Summary = step.Step + "...";
                row.FixProgress = null;
                break;
            case ProgressReported { Fraction: { } fraction } progress:
                row.FixProgress = fraction * 100;
                row.Summary = progress.TotalBytes is { } total
                    ? string.Create(CultureInfo.CurrentCulture, $"{progress.Message} - {fraction:P0} ({(progress.Bytes ?? 0) / 1048576.0:0} of {total / 1048576.0:0} MB)")
                    : string.Create(CultureInfo.CurrentCulture, $"{progress.Message} - {fraction:P0}");
                break;
        }
    }
}
