using System.Text.Json.Nodes;
using Updatr.Core.Errors;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.WinGet;

/// <summary>Runs a winget upgrade/reinstall inside the elevated broker.</summary>
public sealed class WinGetBrokerHandler : IBrokerHandler
{
    public string Type => WinGetComSource.BrokerRunType;

    public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
    {
        string id = payload?["id"]?.GetValue<string>() ?? throw new ArgumentException("Missing package id.");
        bool reinstall = payload?["reinstall"]?.GetValue<bool>() == true;
        bool unknownVersion = payload?["unknownVersion"]?.GetValue<bool>() == true;

        PackageOutcome outcome = payload?["mode"]?.GetValue<string>() == "install"
            ? await WinGetComSource.RunInstallLocalAsync(id, payload?["catalog"]?.GetValue<string>() ?? "winget", id, new EmittingSink(emit)).ConfigureAwait(false)
            : await WinGetComSource.RunLocalAsync(id, reinstall, unknownVersion, id, new EmittingSink(emit)).ConfigureAwait(false);
        return BrokerOutcome.ToJson(outcome);
    }

    /// <summary>Forwards progress to the unelevated client as broker events.</summary>
    private sealed class EmittingSink(Func<JsonNode, Task> emit) : IOperationSink
    {
        public void Report(OperationEvent operationEvent)
        {
            if (operationEvent is ProgressReported progress)
                _ = emit(BrokerProgress.ToJson(progress));
        }

        public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}

/// <summary>Progress events over the broker wire.</summary>
internal static class BrokerProgress
{
    public static JsonObject ToJson(ProgressReported p) => new()
    {
        ["fraction"] = p.Fraction,
        ["bytes"] = p.Bytes,
        ["total"] = p.TotalBytes,
        ["message"] = p.Message,
    };

    public static ProgressReported FromJson(JsonNode e, string itemId) => new(
        itemId,
        e["fraction"]?.GetValue<double>(),
        e["bytes"]?.GetValue<long>(),
        e["total"]?.GetValue<long>(),
        e["message"]?.GetValue<string>());
}

/// <summary>Package outcomes over the broker wire. The code is re-decoded on the client side.</summary>
internal static class BrokerOutcome
{
    public static JsonObject ToJson(PackageOutcome o) => new()
    {
        ["kind"] = o.Kind.ToString(),
        ["detail"] = o.Detail,
        ["reboot"] = o.RebootRequired,
        ["code"] = o.Code?.Code,
    };

    public static PackageOutcome FromJson(JsonNode? reply)
    {
        if (reply is null || !Enum.TryParse(reply["kind"]?.GetValue<string>(), out OutcomeKind kind))
            return PackageOutcome.Failed("The elevated helper returned no result");

        ErrorCodeInfo? code = reply["code"]?.GetValue<uint>() is uint value ? ErrorCodes.Describe(value) : null;
        return new PackageOutcome(kind, reply["detail"]?.GetValue<string>() ?? string.Empty, reply["reboot"]?.GetValue<bool>() == true, code);
    }
}
