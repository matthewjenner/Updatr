using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Platform.Windows;

/// <summary>
/// Holds each install until no other installer is running, instead of colliding with it (MSI
/// error 1618). Watches the MSI install mutex and <c>winget.exe</c> - not Windows servicing
/// (TiWorker), which can run for a long time without blocking app installers.
/// </summary>
public sealed class InstallerGate : IInstallerGate
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    public async Task WaitUntilIdleAsync(IOperationSink sink, string itemId, CancellationToken cancellationToken)
    {
        while (true)
        {
            string[] busy = [.. WindowsInfo.InstallerActivity().Where(b => !b.Contains("TiWorker", StringComparison.Ordinal))];

            if (busy.Length == 0)
                return;

            sink.Report(new ProgressReported(itemId, null, Message: $"Waiting: {string.Join("; ", busy)}"));
            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }
}
