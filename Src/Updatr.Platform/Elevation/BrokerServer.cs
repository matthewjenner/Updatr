using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Updatr.Core.Diagnostics;

namespace Updatr.Platform.Elevation;

/// <summary>
/// The elevated end: accepts exactly one connection - from the process that launched it - and
/// serves typed requests until that process disconnects or exits.
/// </summary>
public sealed class BrokerServer(IReadOnlyList<IBrokerHandler> handlers)
{
    /// <summary>How long the broker waits for its parent to connect before giving up.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, IBrokerHandler> _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);

    /// <summary>Serves one client. Returns the process exit code for the broker.</summary>
    /// <param name="pipeName">Random name the client generated and passed on the command line.</param>
    /// <param name="expectedClientProcessId">The launching process; any other client is refused.</param>
    public async Task<int> RunAsync(string pipeName, int expectedClientProcessId, CancellationToken cancellationToken)
    {
        // FirstPipeInstance: if anything already created this name (pipe squatting), refuse to run.
        await using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityHelpers.CurrentUserOnly());

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ExitWhenParentExitsAsync(expectedClientProcessId, lifetime);

        using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            connect.CancelAfter(ConnectTimeout);

            try
            {
                await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                DiagLog.BrokerExited("the client never connected");
                return 2;
            }
        }

        int client = PipeSecurityHelpers.ClientProcessId(pipe);

        if (client != expectedClientProcessId)
        {
            DiagLog.BrokerRejectedClient($"connection from PID {client}, expected {expectedClientProcessId}");
            return 3;
        }

        await ServeAsync(pipe, lifetime.Token).ConfigureAwait(false);
        return 0;
    }

    private async Task ServeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true);
        SemaphoreSlim writeGate = new(1, 1);
        ConcurrentDictionary<int, CancellationTokenSource> running = new();
        List<Task> work = [];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                BrokerMessage? message = await BrokerWire.ReadAsync(reader, cancellationToken).ConfigureAwait(false);

                if (message is null)
                {
                    DiagLog.BrokerExited("the client disconnected");
                    break;
                }

                if (message.Kind == BrokerMessage.Cancel && running.TryGetValue(message.Id, out CancellationTokenSource? toCancel))
                {
                    await toCancel.CancelAsync().ConfigureAwait(false);
                }
                else if (message.Kind == BrokerMessage.Request)
                {
                    CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    running[message.Id] = requestCts;
                    work.Add(HandleAsync(message, writer, writeGate, requestCts.Token).ContinueWith(completed =>
                    {
                        running.TryRemove(message.Id, out _);
                        requestCts.Dispose();
                    }, TaskScheduler.Default));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagLog.BrokerExited("the client process exited");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            DiagLog.BrokerExited($"the connection broke ({ex.Message})");
        }

        foreach (CancellationTokenSource cts in running.Values)
            await cts.CancelAsync().ConfigureAwait(false);

        await Task.WhenAll(work).ConfigureAwait(false);
    }

    private async Task HandleAsync(BrokerMessage request, StreamWriter writer, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        BrokerMessage response;

        if (request.Type is null || !_handlers.TryGetValue(request.Type, out IBrokerHandler? handler))
        {
            response = new BrokerMessage { Kind = BrokerMessage.Response, Id = request.Id, Error = $"Unknown request type '{request.Type}'." };
        }
        else
        {
            try
            {
                JsonNode? result = await handler.HandleAsync(
                    request.Payload,
                    e => BrokerWire.WriteAsync(writer, gate, new BrokerMessage { Kind = BrokerMessage.Event, Id = request.Id, Payload = e }, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                response = new BrokerMessage { Kind = BrokerMessage.Response, Id = request.Id, Payload = result };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response = new BrokerMessage { Kind = BrokerMessage.Response, Id = request.Id, Error = "Canceled." };
            }
            catch (Exception ex)
            {
                response = new BrokerMessage { Kind = BrokerMessage.Response, Id = request.Id, Error = ex.Message };
            }
        }

        DiagLog.BrokerRequest(request.Type ?? "(none)", response.Error is null, timer.Elapsed);

        try
        {
            await BrokerWire.WriteAsync(writer, gate, response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client gone; nothing to tell it.
        }
    }

    private static async Task ExitWhenParentExitsAsync(int parentProcessId, CancellationTokenSource lifetime)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await lifetime.CancelAsync().ConfigureAwait(false); // Parent already gone.
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
