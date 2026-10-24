using System.Globalization;
using Updatr.Core.Diagnostics;
using Updatr.Platform.Data;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Elevation;

/// <summary>
/// Entry point for broker mode. Both exes call <see cref="TryRun"/> first thing in Main: when
/// launched with <c>--broker</c> the process serves the parent and exits, never starting a UI.
/// </summary>
public static class BrokerHost
{
    public const string BrokerSwitch = "--broker";
    public const string ParentSwitch = "--parent";
    public const string DataDirSwitch = "--data-dir";

    /// <summary>Returns the exit code if <paramref name="args"/> asked for broker mode, otherwise null.</summary>
    public static int? TryRun(string[] args)
    {
        int index = Array.IndexOf(args, BrokerSwitch);

        if (index < 0)
            return null;

        int parentIndex = Array.IndexOf(args, ParentSwitch);

        if (index + 1 >= args.Length || parentIndex < 0 || parentIndex + 1 >= args.Length
            || !int.TryParse(args[parentIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
        {
            return 64; // Malformed - someone other than Updatr started us this way.
        }

        DiagLog.Started("broker", Core.ProductInfo.Version, WindowsInfo.Os().ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), WindowsInfo.IsElevated);

        // The broker's own log goes beside the host's, so elevated work is on the drive too.
        int dataIndex = Array.IndexOf(args, DataDirSwitch);
        RunLogWriter? log = dataIndex >= 0 && dataIndex + 1 < args.Length
            ? new RunLogWriter(new PortableDataStore(args[dataIndex + 1]), "broker")
            : null;

        try
        {
            return new BrokerServer(BrokerHandlers.All).RunAsync(args[index + 1], parent, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            log?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
