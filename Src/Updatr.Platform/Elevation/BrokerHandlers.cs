using System.Text.Json.Nodes;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Elevation;

/// <summary>
/// One kind of work the elevated broker will do. The set is closed - <see cref="BrokerHandlers.All"/>
/// - so a compromised or confused client can ask only for these, never run an arbitrary command.
/// </summary>
public interface IBrokerHandler
{
    /// <summary>Request type on the wire.</summary>
    string Type { get; }

    /// <summary>
    /// Does the work. Progress goes through <paramref name="emit"/>; the return value is the
    /// response payload. Throwing sends a failed response with the exception message.
    /// </summary>
    Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken);
}

/// <summary>The handlers the broker serves. Admin-only features add theirs here.</summary>
public static class BrokerHandlers
{
    public static IReadOnlyList<IBrokerHandler> All { get; } =
    [
        new PingHandler(),
    ];
}

/// <summary>Proves the channel works and reports what the broker is running as.</summary>
public sealed class PingHandler : IBrokerHandler
{
    public const string TypeName = "ping";

    public string Type => TypeName;

    public Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
        => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["elevated"] = WindowsInfo.IsElevated,
            ["echo"] = payload?.DeepClone(),
        });
}
