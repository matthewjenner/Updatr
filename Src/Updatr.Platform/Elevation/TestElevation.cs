using System.Diagnostics;
using System.Text.Json.Nodes;
using Updatr.Core.Operations;

namespace Updatr.Platform.Elevation;

/// <summary>What the elevation test found.</summary>
/// <param name="Succeeded">The broker started elevated and answered.</param>
/// <param name="Summary">One line for the user.</param>
public sealed record ElevationTestResult(bool Succeeded, string Summary);

/// <summary>
/// Requests elevation (one UAC prompt), pings the broker, and reports what it is running as.
/// Proves on a given machine that admin-only steps will work, before a batch depends on it.
/// </summary>
[Operation("admin test")]
public sealed class TestElevation(ElevationService elevation) : IOperation<bool, ElevationTestResult>
{
    /// <param name="request">Ignored; there is nothing to configure.</param>
    public async Task<ElevationTestResult> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
    {
        sink.Report(new StepStarted("Requesting administrator access - approve the UAC prompt"));

        // An explicit test should ask again even if an earlier prompt this session was declined.
        elevation.ResetDeclined();
        Stopwatch timer = Stopwatch.StartNew();

        BrokerClient? broker;

        try
        {
            broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerException ex)
        {
            return new ElevationTestResult(false, $"The elevated helper failed to start: {ex.Message}");
        }

        if (broker is null)
            return new ElevationTestResult(false, "Declined at the UAC prompt. Admin-only steps will be skipped until you approve it.");

        JsonNode? reply = await broker.SendAsync(PingHandler.TypeName, null, null, cancellationToken).ConfigureAwait(false);
        bool elevated = reply?["elevated"]?.GetValue<bool>() == true;
        int pid = reply?["processId"]?.GetValue<int>() ?? 0;

        return elevated
            ? new ElevationTestResult(true, $"Working: elevated helper answered (PID {pid}, {timer.ElapsedMilliseconds} ms including the prompt).")
            : new ElevationTestResult(false, $"The helper answered (PID {pid}) but is not elevated - admin-only steps would fail.");
    }
}
