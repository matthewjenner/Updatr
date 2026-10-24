using System.Diagnostics;
using Updatr.Core.Diagnostics;
using Updatr.Core.Operations;

namespace Updatr.Core.Preflight;

/// <summary>
/// Runs the startup checks: the splash screen's rows and <c>updatr-cli preflight</c>.
/// </summary>
/// <remarks>
/// Checks run in waves: every check whose dependencies are done runs in parallel with the others
/// in its wave, so the slow ones (network, winget) overlap and the splash fills in as results land.
/// A check that throws becomes a Fail row - one broken check never hides the rest.
/// </remarks>
[Operation("preflight")]
public sealed class RunPreflight(IReadOnlyList<IPreflightCheck> checks) : IOperation<PreflightRequest, PreflightReport>
{
    /// <summary>The checks this operation knows about, in display order.</summary>
    public IReadOnlyList<IPreflightCheck> Checks => checks;

    public async Task<PreflightReport> RunAsync(PreflightRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Stopwatch total = Stopwatch.StartNew();
        List<IPreflightCheck> selected = Select(request.Only);
        PreflightContext context = new();
        Dictionary<string, CheckFinished> finished = new(StringComparer.Ordinal);
        HashSet<string> pending = [.. selected.Select(c => c.Id)];

        while (pending.Count > 0)
        {
            List<IPreflightCheck> wave = [.. selected.Where(c =>
                pending.Contains(c.Id) && c.DependsOn.All(d => !pending.Contains(d)))];

            if (wave.Count == 0)
                throw new InvalidOperationException("Preflight checks have a dependency cycle: " + string.Join(", ", pending));

            CheckFinished[] results = await Task.WhenAll(wave.Select(c => RunOneAsync(c, context, sink, cancellationToken))).ConfigureAwait(false);

            foreach (CheckFinished result in results)
            {
                finished[result.CheckId] = result;
                pending.Remove(result.CheckId);
            }
        }

        List<CheckFinished> ordered = [.. selected.Select(c => finished[c.Id])];
        DiagLog.PreflightFinished(
            ordered.Count(r => r.Result.Status is PreflightStatus.Pass or PreflightStatus.Info),
            ordered.Count(r => r.Result.Status == PreflightStatus.Warn),
            ordered.Count(r => r.Result.Status == PreflightStatus.Fail),
            total.Elapsed);

        return new PreflightReport(ordered, context);
    }

    private List<IPreflightCheck> Select(IReadOnlyCollection<string>? only)
    {
        if (only is null)
            return [.. checks];

        Dictionary<string, IPreflightCheck> byId = checks.ToDictionary(c => c.Id, StringComparer.Ordinal);
        HashSet<string> wanted = new(StringComparer.Ordinal);
        Stack<string> toVisit = new(only);

        while (toVisit.Count > 0)
        {
            string id = toVisit.Pop();

            if (!byId.TryGetValue(id, out IPreflightCheck? check))
                throw new ArgumentException($"Unknown preflight check '{id}'. Known: {string.Join(", ", byId.Keys)}.");

            if (wanted.Add(id))
            {
                foreach (string dependency in check.DependsOn)
                    toVisit.Push(dependency);
            }
        }

        return [.. checks.Where(c => wanted.Contains(c.Id))];
    }

    private static async Task<CheckFinished> RunOneAsync(IPreflightCheck check, PreflightContext context, IOperationSink sink, CancellationToken cancellationToken)
    {
        sink.Report(new CheckStarted(check.Id, check.Title));
        Stopwatch timer = Stopwatch.StartNew();
        PreflightResult result;

        try
        {
            // Off the caller's thread: several checks are synchronous underneath (registry, COM).
            result = await Task.Run(() => check.RunAsync(context, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagLog.PreflightCheckCrashed(check.Id, ex);
            result = PreflightResult.Fail($"The check itself failed: {ex.Message}", ex.ToString());
        }

        CheckFinished finished = new(check.Id, check.Title, result, timer.Elapsed);
        DiagLog.PreflightCheck(check.Id, result.Status.ToString(), result.Summary, timer.Elapsed);
        sink.Report(finished);
        return finished;
    }
}
