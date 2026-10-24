using System.Text.Json.Nodes;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.Tests.Elevation;

/// <summary>
/// The broker protocol over a real named pipe, both ends in this process (so the PID checks pass
/// as they would between Updatr and its broker). Real elevation needs a UAC click and is tested by
/// hand; everything else about the channel is tested here.
/// </summary>
public sealed class BrokerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewPipe() => $"Updatr-test-{Guid.NewGuid():N}";

    private static async Task<(BrokerClient Client, Task<int> Server)> StartAsync(params IBrokerHandler[] handlers)
    {
        string pipe = NewPipe();
        IReadOnlyList<IBrokerHandler> all = [.. BrokerHandlers.All, .. handlers];
        Task<int> server = Task.Run(() => new BrokerServer(all).RunAsync(pipe, Environment.ProcessId, Ct));
        BrokerClient client = await BrokerClient.ConnectAsync(pipe, Environment.ProcessId, Ct);
        return (client, server);
    }

    [Fact]
    public async Task PingRoundTrips()
    {
        (BrokerClient client, Task<int> server) = await StartAsync();

        await using (client)
        {
            JsonNode? reply = await client.SendAsync(PingHandler.TypeName, new JsonObject { ["hello"] = "broker" }, null, Ct);

            reply!["processId"]!.GetValue<int>().Should().Be(Environment.ProcessId);
            reply["echo"]!["hello"]!.GetValue<string>().Should().Be("broker");
        }

        (await server.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(0, "the broker exits cleanly when the client disconnects");
    }

    [Fact]
    public async Task UnknownRequestTypesAreRefused()
    {
        (BrokerClient client, _) = await StartAsync();
        await using BrokerClient owned = client;

        Func<Task> send = () => client.SendAsync("format-c-drive", null, null, Ct);

        (await send.Should().ThrowAsync<BrokerException>()).Which.Message.Should().Contain("Unknown request type");
    }

    [Fact]
    public async Task EventsArriveBeforeTheResponse()
    {
        (BrokerClient client, _) = await StartAsync(new CountingHandler());
        await using BrokerClient owned = client;
        List<int> seen = [];

        JsonNode? reply = await client.SendAsync(CountingHandler.TypeName, null, e => { lock (seen) seen.Add(e["n"]!.GetValue<int>()); }, Ct);

        reply!.GetValue<string>().Should().Be("done");
        seen.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task HandlerFailuresComeBackAsBrokerExceptions()
    {
        (BrokerClient client, _) = await StartAsync(new ThrowingHandler());
        await using BrokerClient owned = client;

        Func<Task> send = () => client.SendAsync(ThrowingHandler.TypeName, null, null, Ct);

        (await send.Should().ThrowAsync<BrokerException>()).Which.Message.Should().Be("registry key is locked");
    }

    [Fact]
    public async Task CancelingARequestCancelsItInTheBroker()
    {
        WaitingHandler waiting = new();
        (BrokerClient client, _) = await StartAsync(waiting);
        await using BrokerClient owned = client;
        using CancellationTokenSource cts = new();

        Task send = client.SendAsync(WaitingHandler.TypeName, null, null, cts.Token);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        await send.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        (await waiting.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task TheClientRefusesAPipeServedByTheWrongProcess()
    {
        string pipe = NewPipe();
        _ = Task.Run(() => new BrokerServer(BrokerHandlers.All).RunAsync(pipe, Environment.ProcessId, Ct));

        Func<Task> connect = () => BrokerClient.ConnectAsync(pipe, expectedBrokerProcessId: Environment.ProcessId + 1, Ct);

        (await connect.Should().ThrowAsync<BrokerException>()).Which.Message.Should().Contain("Refusing");
    }

    [Fact]
    public async Task TheServerRefusesAClientThatIsNotItsParent()
    {
        // A real, separate process plays the broker's parent, so this test process is genuinely
        // someone else. (Not ProcessId + 1: Windows ignores a PID's low two bits, so that number
        // opens this very process.)
        using System.Diagnostics.Process parent = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

        try
        {
            string pipe = NewPipe();
            Task<int> server = Task.Run(() => new BrokerServer(BrokerHandlers.All).RunAsync(pipe, expectedClientProcessId: parent.Id, Ct));

            try
            {
                await using BrokerClient client = await BrokerClient.ConnectAsync(pipe, Environment.ProcessId, Ct);
                Func<Task> ping = () => client.SendAsync(PingHandler.TypeName, null, null, Ct);
                await ping.Should().ThrowAsync<BrokerException>("a refused client must not get answers");
            }
            catch (Exception ex) when (ex is BrokerException or IOException)
            {
                // Dropped while connecting - also a refusal.
            }

            (await server.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(3, "3 is the broker's 'wrong client' exit code");
        }
        finally
        {
            parent.Kill();
        }
    }

    [Fact]
    public async Task ASecondServerCannotTakeOverAPipeName()
    {
        string pipe = NewPipe();
        Task<int> first = Task.Run(() => new BrokerServer(BrokerHandlers.All).RunAsync(pipe, Environment.ProcessId, Ct));
        await using BrokerClient client = await BrokerClient.ConnectAsync(pipe, Environment.ProcessId, Ct);

        Func<Task> squat = () => new BrokerServer(BrokerHandlers.All).RunAsync(pipe, Environment.ProcessId, Ct);

        await squat.Should().ThrowAsync<Exception>();
        first.IsCompleted.Should().BeFalse();
    }

    private sealed class CountingHandler : IBrokerHandler
    {
        public const string TypeName = "test-count";

        public string Type => TypeName;

        public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
        {
            for (int i = 1; i <= 3; i++)
                await emit(new JsonObject { ["n"] = i });

            return JsonValue.Create("done");
        }
    }

    private sealed class ThrowingHandler : IBrokerHandler
    {
        public const string TypeName = "test-throw";

        public string Type => TypeName;

        public Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
            => throw new UnauthorizedAccessException("registry key is locked");
    }

    private sealed class WaitingHandler : IBrokerHandler
    {
        public const string TypeName = "test-wait";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Type => TypeName;

        public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
        {
            Started.SetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.SetResult(true);
                throw;
            }

            return null;
        }
    }
}
