using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Updatr.Platform.Elevation;

/// <summary>
/// Wire format between Updatr and its elevated broker: one JSON object per line over a named pipe.
/// </summary>
/// <remarks>
/// <c>request</c> (client to broker) carries a handler <c>type</c> and payload; the broker answers
/// with exactly one <c>response</c> for the same <c>id</c>, possibly preceded by <c>event</c>s
/// (progress) for that id. <c>cancel</c> asks the broker to cancel a running request.
/// </remarks>
public sealed record BrokerMessage
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
    public const string Cancel = "cancel";

    public required string Kind { get; init; }

    public required int Id { get; init; }

    /// <summary>Handler type, on requests.</summary>
    public string? Type { get; init; }

    public JsonNode? Payload { get; init; }

    /// <summary>Set on a failed response; the payload is then empty.</summary>
    public string? Error { get; init; }
}

internal static class BrokerWire
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Longest line accepted. A peer sending more is broken or hostile; the connection is dropped.</summary>
    public const int MaxLineLength = 4 * 1024 * 1024;

    public static async Task WriteAsync(StreamWriter writer, SemaphoreSlim gate, BrokerMessage message, CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(message, Options);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads the next message, or null at end of stream.</summary>
    public static async Task<BrokerMessage?> ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (line is null)
            return null;

        if (line.Length > MaxLineLength)
            throw new InvalidDataException("Broker message too long.");

        return JsonSerializer.Deserialize<BrokerMessage>(line, Options) ?? throw new InvalidDataException("Empty broker message.");
    }
}

/// <summary>A request the broker failed, or a broker that went away.</summary>
public sealed class BrokerException(string message, Exception? inner = null) : Exception(message, inner);
