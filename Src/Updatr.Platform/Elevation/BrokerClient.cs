using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Updatr.Core.Diagnostics;

namespace Updatr.Platform.Elevation;

/// <summary>
/// The unelevated end. <see cref="LaunchAsync"/> starts this exe again elevated (one UAC prompt)
/// as a broker and connects to it; <see cref="SendAsync"/> runs a request there.
/// </summary>
public sealed class BrokerClient : IAsyncDisposable
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said no at the UAC prompt.

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private int _nextId;

    private BrokerClient(NamedPipeClientStream pipe, int brokerProcessId)
    {
        _pipe = pipe;
        BrokerProcessId = brokerProcessId;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public int BrokerProcessId { get; }

    /// <summary>False once the broker has gone away.</summary>
    public bool IsConnected => _pipe.IsConnected && !_readLoop.IsCompleted;

    /// <summary>
    /// Starts <paramref name="exePath"/> elevated as a broker and connects to it. Returns null when
    /// the user declines the UAC prompt.
    /// </summary>
    /// <param name="dataRoot">The host's data folder, so the broker writes its log next to the host's.</param>
    public static async Task<BrokerClient?> LaunchAsync(string exePath, string? dataRoot, CancellationToken cancellationToken)
    {
        string pipeName = $"Updatr-broker-{Guid.NewGuid():N}";
        DiagLog.BrokerLaunching(pipeName);

        ProcessStartInfo info = new(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            // The pipe name and PID are generated here; the data folder is a Windows path, which
            // cannot contain quotes - only a trailing backslash would escape the closing quote.
            Arguments = $"{BrokerHost.BrokerSwitch} {pipeName} {BrokerHost.ParentSwitch} {Environment.ProcessId}"
                + (dataRoot is null ? string.Empty : $" {BrokerHost.DataDirSwitch} \"{dataRoot.TrimEnd('\\')}\""),
        };

        Process? broker;

        try
        {
            broker = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            DiagLog.BrokerDeclined();
            return null;
        }

        if (broker is null)
            throw new BrokerException("The elevated broker did not start.");

        using (broker)
            return await ConnectAsync(pipeName, broker.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Connects to a broker that is already serving <paramref name="pipeName"/>.</summary>
    /// <param name="expectedBrokerProcessId">The broker's PID; a pipe served by anything else is refused.</param>
    public static async Task<BrokerClient> ConnectAsync(string pipeName, int expectedBrokerProcessId, CancellationToken cancellationToken)
    {
        NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(BrokerServer.ConnectTimeout);
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);

            int server = PipeSecurityHelpers.ServerProcessId(pipe);

            if (server != expectedBrokerProcessId)
                throw new BrokerException($"The broker pipe is served by PID {server}, expected {expectedBrokerProcessId}. Refusing to use it.");

            DiagLog.BrokerConnected(server);
            return new BrokerClient(pipe, server);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BrokerException("The elevated broker did not answer.");
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs a request in the broker and returns its response payload.</summary>
    /// <param name="type">Handler type (see <see cref="BrokerHandlers"/>).</param>
    /// <param name="payload">Request payload.</param>
    /// <param name="onEvent">Progress events for this request, called on a background thread.</param>
    public async Task<JsonNode?> SendAsync(string type, JsonNode? payload, Action<JsonNode>? onEvent, CancellationToken cancellationToken)
    {
        int id = Interlocked.Increment(ref _nextId);
        Pending pending = new(onEvent);
        _pending[id] = pending;

        bool sent = false;

        try
        {
            await BrokerWire.WriteAsync(_writer, _writeGate, new BrokerMessage { Kind = BrokerMessage.Request, Id = id, Type = type, Payload = payload }, cancellationToken).ConfigureAwait(false);
            sent = true;
            BrokerMessage response = await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return response.Error is null ? response.Payload : throw new BrokerException(response.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && sent)
        {
            // Tell the broker to stop the work too. Sent here rather than from a token callback:
            // callbacks run newest-first, so WaitAsync's own callback resumes this method - and
            // would dispose such a registration - before it ever ran (found by BrokerTests).
            await TrySendCancelAsync(id).ConfigureAwait(false);
            throw;
        }
        catch (IOException ex)
        {
            throw new BrokerException("The elevated broker went away.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task TrySendCancelAsync(int id)
    {
        try
        {
            await BrokerWire.WriteAsync(_writer, _writeGate, new BrokerMessage { Kind = BrokerMessage.Cancel, Id = id }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The broker is gone, so the work is gone with it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false); // The broker sees EOF and exits.

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Shutting down.
        }

        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await BrokerWire.ReadAsync(_reader, _lifetime.Token).ConfigureAwait(false) is { } message)
            {
                if (!_pending.TryGetValue(message.Id, out Pending? pending))
                    continue;

                if (message.Kind == BrokerMessage.Event && message.Payload is not null)
                    pending.OnEvent?.Invoke(message.Payload);
                else if (message.Kind == BrokerMessage.Response)
                    pending.Completion.TrySetResult(message);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException)
        {
            // Connection ended; fail whatever is still waiting below.
        }

        foreach (Pending pending in _pending.Values)
            pending.Completion.TrySetException(new BrokerException("The elevated broker went away."));
    }

    private sealed record Pending(Action<JsonNode>? OnEvent)
    {
        public TaskCompletionSource<BrokerMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
