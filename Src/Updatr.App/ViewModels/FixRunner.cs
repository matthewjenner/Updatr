using Updatr.Core.Preflight;
using Updatr.Platform;
using Updatr.Platform.WinGet;

namespace Updatr.App.ViewModels;

/// <summary>Maps a preflight fix id to the operation that performs it - the same one the CLI runs.</summary>
internal static class FixRunner
{
    /// <summary>Runs the fix off the UI thread and returns a one-line result for the user.</summary>
    public static async Task<string> RunAsync(UpdatrHost host, PreflightFix fix)
    {
        try
        {
            return fix.Id switch
            {
                RepairWinGet.FixId => (await Task.Run(() => host.RepairWinGet.RunAsync(true, new Services.GuiSink(_ => { }), CancellationToken.None))).Summary,
                _ => $"No fix is available here for '{fix.Id}'. Try: {fix.CliCommand}",
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{fix.Label} failed: {ex.Message}";
        }
    }
}
