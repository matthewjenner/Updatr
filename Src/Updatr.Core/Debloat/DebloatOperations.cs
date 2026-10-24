using Updatr.Core.Operations;
using Updatr.Core.Packages;

namespace Updatr.Core.Debloat;

/// <summary>Live state of every debloat item, plus unrecognized Store apps.</summary>
[Operation("debloat status")]
public sealed class DebloatStatus(DebloatEngine engine) : IOperation<bool, DebloatReport>
{
    public Task<DebloatReport> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
        => engine.StatusAsync(cancellationToken);
}

/// <summary>Applies the recommended set, picked items, and/or removes unrecognized apps. Only what has drifted is changed.</summary>
[Operation("debloat apply")]
public sealed class ApplyDebloat(DebloatEngine engine) : IOperation<DebloatApplyRequest, RunSummary>
{
    public Task<RunSummary> RunAsync(DebloatApplyRequest request, IOperationSink sink, CancellationToken cancellationToken)
        => engine.ApplyAsync(request, sink, cancellationToken);
}

/// <summary>Puts items back the way they were before Updatr last applied them, from the backup.</summary>
[Operation("debloat revert")]
public sealed class RevertDebloat(DebloatEngine engine) : IOperation<IReadOnlyList<string>, RunSummary>
{
    public Task<RunSummary> RunAsync(IReadOnlyList<string> request, IOperationSink sink, CancellationToken cancellationToken)
        => engine.RevertAsync(request, sink, cancellationToken);
}

/// <summary>What debloat has applied and reverted, oldest first.</summary>
[Operation("debloat history")]
public sealed class DebloatHistory(DebloatStore store) : IOperation<bool, IReadOnlyList<HistoryEntry>>
{
    public Task<IReadOnlyList<HistoryEntry>> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
        => store.LoadHistoryAsync(cancellationToken);
}

/// <summary>Adds Store app names (wildcards allowed) to the keep list, so they stop showing as unrecognized.</summary>
[Operation("debloat keep")]
public sealed class KeepApps(DebloatStore store) : IOperation<IReadOnlyList<string>, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> request, IOperationSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keep = await store.LoadKeepAsync(cancellationToken).ConfigureAwait(false);
        string[] updated = [.. keep.Concat(request.Select(n => n.Trim()).Where(n => n.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        await store.SaveKeepAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}

/// <summary>Removes names from the keep list.</summary>
[Operation("debloat unkeep")]
public sealed class UnkeepApps(DebloatStore store) : IOperation<IReadOnlyList<string>, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> request, IOperationSink sink, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keep = await store.LoadKeepAsync(cancellationToken).ConfigureAwait(false);
        string[] updated = [.. keep.Where(k => !request.Contains(k, StringComparer.OrdinalIgnoreCase))];
        await store.SaveKeepAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
