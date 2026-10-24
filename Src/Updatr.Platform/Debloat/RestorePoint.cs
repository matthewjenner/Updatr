using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Updatr.Core.Debloat;
using Updatr.Platform.Elevation;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Debloat;

/// <summary>
/// A System Restore point through <c>SRSetRestorePointW</c> (administrator only, so normally in the
/// broker). Best effort: Windows throttles them (one per 24 hours by default) and many machines
/// have System Restore off - Updatr's own backup is the real undo.
/// </summary>
public sealed partial class RestorePoint(ElevationService elevation) : IRestorePoint
{
    public const string BrokerType = "restore-point";

    public async Task<string?> CreateAsync(string description, CancellationToken cancellationToken)
    {
        if (WindowsInfo.IsElevated)
            return await Task.Run(() => CreateLocal(description), cancellationToken).ConfigureAwait(false);

        BrokerClient? broker = await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

        if (broker is null)
            return "administrator access was declined";

        try
        {
            JsonNode? reply = await broker.SendAsync(BrokerType, new JsonObject { ["description"] = description }, null, cancellationToken).ConfigureAwait(false);
            return reply?["problem"]?.GetValue<string>();
        }
        catch (BrokerException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Null on success, otherwise why not.</summary>
    internal static unsafe string? CreateLocal(string description)
    {
        RestorePointInfo info = new() { EventType = BeginSystemChange, RestorePointType = ModifySettings };
        string text = description.Length >= DescriptionLength ? description[..(DescriptionLength - 1)] : description;

        for (int i = 0; i < text.Length; i++)
            info.Description[i] = text[i];

        try
        {
            if (!SRSetRestorePointW(info, out StateManagerStatus status))
                return status.Status == ServiceDisabled ? "System Restore is turned off on this PC" : new Win32Exception((int)status.Status).Message;

            // Close the begin/end pair so the point is finished, not left "in progress".
            RestorePointInfo end = new() { EventType = EndSystemChange, RestorePointType = ModifySettings, SequenceNumber = status.SequenceNumber };
            SRSetRestorePointW(end, out _);
            return null;
        }
        catch (DllNotFoundException)
        {
            return "System Restore is not available on this edition";
        }
    }

    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ModifySettings = 12;
    private const uint ServiceDisabled = 1058;
    private const int DescriptionLength = 256;

    // srrestoreptapi.h packs these to 1 byte.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;
        public fixed char Description[DescriptionLength];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StateManagerStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [LibraryImport("srclient.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SRSetRestorePointW(in RestorePointInfo info, out StateManagerStatus status);
}

/// <summary>Creates the restore point inside the elevated broker.</summary>
public sealed class RestorePointBrokerHandler : IBrokerHandler
{
    public string Type => RestorePoint.BrokerType;

    public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
    {
        string description = payload?["description"]?.GetValue<string>() ?? "Updatr";
        string? problem = await Task.Run(() => RestorePoint.CreateLocal(description), cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["problem"] = problem };
    }
}
