using System.Text.Json.Nodes;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.Windows;
using Updatr.Platform.WinGet;

namespace Updatr.Platform.Debloat;

/// <summary>
/// Runs the admin half of a debloat change: in this process when it is already elevated, otherwise
/// through the broker (<see cref="DebloatBrokerHandler"/>).
/// </summary>
internal static class DebloatElevation
{
    public const string BrokerType = "debloat-run";

    public static Task<PackageOutcome> ApplyAsync(ElevationService elevation, DebloatAction action, Func<Task<PackageOutcome>> local)
        => RunAsync(elevation, new JsonObject { ["op"] = "apply", ["action"] = DebloatJson.Serialize(action) }, local);

    public static Task<PackageOutcome> RevertAsync(ElevationService elevation, ActionBackup backup, Func<Task<PackageOutcome>> local)
        => RunAsync(elevation, new JsonObject { ["op"] = "revert", ["backup"] = DebloatJson.Serialize(backup) }, local);

    public static Task<PackageOutcome> DeprovisionAsync(ElevationService elevation, AppxAction action, Func<Task<PackageOutcome>> local)
        => RunAsync(elevation, new JsonObject { ["op"] = "deprovision", ["action"] = DebloatJson.Serialize(action) }, local);

    private static async Task<PackageOutcome> RunAsync(ElevationService elevation, JsonObject payload, Func<Task<PackageOutcome>> local)
    {
        if (WindowsInfo.IsElevated)
            return await local().ConfigureAwait(false);

        BrokerClient? broker = await elevation.GetBrokerAsync(CancellationToken.None).ConfigureAwait(false);

        if (broker is null)
            return PackageOutcome.Skipped("Needs administrator access, which was declined");

        try
        {
            return BrokerOutcome.FromJson(await broker.SendAsync(BrokerType, payload, null, CancellationToken.None).ConfigureAwait(false));
        }
        catch (BrokerException ex)
        {
            return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Debloat changes inside the elevated broker. Only the action kinds and places debloat rules use
/// are accepted, so the broker cannot be talked into writing arbitrary machine state.
/// </summary>
public sealed class DebloatBrokerHandler : IBrokerHandler
{
    public string Type => DebloatElevation.BrokerType;

    public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
    {
        string op = payload?["op"]?.GetValue<string>() ?? throw new ArgumentException("Missing op.");

        PackageOutcome outcome = op switch
        {
            "apply" => await ApplyAsync(Allowed(DebloatJson.DeserializeAction(payload?["action"]?.GetValue<string>() ?? "null"))).ConfigureAwait(false),
            "revert" => await RevertAsync(DebloatJson.DeserializeBackup(payload?["backup"]?.GetValue<string>() ?? "null") ?? throw new ArgumentException("Missing backup.")).ConfigureAwait(false),
            "deprovision" when Allowed(DebloatJson.DeserializeAction(payload?["action"]?.GetValue<string>() ?? "null")) is AppxAction appx
                => await AppxTarget.DeprovisionLocalAsync(appx).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown debloat op '{op}'."),
        };

        return BrokerOutcome.ToJson(outcome);
    }

    private static Task<PackageOutcome> ApplyAsync(DebloatAction action) => action switch
    {
        RegistryAction r => Task.FromResult(RegistryTarget.ApplyLocal(r)),
        ServiceAction s => Task.FromResult(ServiceTarget.ApplyLocal(s)),
        ScheduledTaskAction t => Task.FromResult(ScheduledTaskTarget.SetEnabledLocal(t, enabled: false)),
        _ => throw new ArgumentException($"{action.GetType().Name} is not applied by the elevated helper."),
    };

    private static Task<PackageOutcome> RevertAsync(ActionBackup backup) => Allowed(backup.Action) switch
    {
        RegistryAction r => Task.FromResult(RegistryTarget.RevertLocal(r, backup)),
        ServiceAction s => Task.FromResult(ServiceTarget.RevertLocal(s, backup)),
        ScheduledTaskAction t => Task.FromResult(ScheduledTaskTarget.SetEnabledLocal(t, enabled: backup.Value != "disabled")),
        _ => throw new ArgumentException($"{backup.Action.GetType().Name} is not reverted by the elevated helper."),
    };

    /// <summary>The action, if it is well formed and inside what debloat rules may touch; throws otherwise.</summary>
    internal static DebloatAction Allowed(DebloatAction? action)
    {
        if (action is null)
            throw new ArgumentException("Missing action.");

        if (CatalogValidator.ValidateAction(action) is { } problem)
            throw new ArgumentException(problem);

        bool inScope = action switch
        {
            RegistryAction r => RegistryTarget.NeedsAdminKey(r),
            ServiceAction => true,
            ScheduledTaskAction t => t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase),
            AppxAction => true,
            _ => false,
        };

        return inScope ? action : throw new ArgumentException($"'{action.Describe}' is outside what debloat may change.");
    }
}
