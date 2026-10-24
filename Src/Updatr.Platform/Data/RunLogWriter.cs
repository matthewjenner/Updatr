using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Updatr.Core.Abstractions;
using Updatr.Core.Diagnostics;

namespace Updatr.Platform.Data;

/// <summary>
/// Writes every <see cref="DiagLog"/> entry of this run to <c>logs/&lt;timestamp&gt;-&lt;front end&gt;.log</c>
/// in the data folder, so a failure on a machine you have left can be read later from the drive.
/// </summary>
/// <remarks>
/// Batched (one append per second at most) because the drive may be slow USB. The first write
/// failure turns the writer off for the rest of the run: logging a log failure would loop, and the
/// data store has already recorded the problem for the banner.
/// </remarks>
public sealed class RunLogWriter : IAsyncDisposable
{
    /// <summary>Log files kept; older ones are deleted at startup.</summary>
    public const int KeepFiles = 30;

    private readonly IDataStore _store;
    private readonly string _path;
    private readonly Channel<LogEntry> _queue = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private bool _disabled;

    public RunLogWriter(IDataStore store, string frontEnd)
    {
        _store = store;
        _path = string.Create(CultureInfo.InvariantCulture, $"logs/{DateTime.Now:yyyyMMdd-HHmmss}-{frontEnd}.log");

        // Entries emitted before the writer existed (host start-up) belong in this file too.
        foreach (LogEntry entry in DiagLog.Snapshot())
            _queue.Writer.TryWrite(entry);

        DiagLog.EntryAdded += OnEntry;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The log file, relative to the data folder.</summary>
    public string RelativePath => _path;

    /// <summary>Deletes all but the newest <see cref="KeepFiles"/> logs. Best effort.</summary>
    public static async Task PruneAsync(IDataStore store, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> logs = await store.ListAsync("logs", cancellationToken).ConfigureAwait(false);

            // File names start with a sortable timestamp.
            foreach (string old in logs.Where(l => l.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).SkipLast(KeepFiles))
                await store.DeleteAsync(old, cancellationToken).ConfigureAwait(false);
        }
        catch (DataStoreException)
        {
            // Read-only drive: nothing to prune, nothing to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        DiagLog.EntryAdded -= OnEntry;
        _queue.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }

    private void OnEntry(LogEntry entry) => _queue.Writer.TryWrite(entry);

    private async Task PumpAsync()
    {
        StringBuilder batch = new();

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out LogEntry? entry))
                batch.AppendLine(entry.ToString());

            if (!_disabled && batch.Length > 0)
            {
                try
                {
                    await _store.AppendTextAsync(_path, batch.ToString(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (DataStoreException)
                {
                    _disabled = true;
                }
            }

            batch.Clear();

            // Coalesce bursts (a preflight emits a dozen lines at once) into one write.
            if (!_queue.Reader.Completion.IsCompleted)
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }
}
