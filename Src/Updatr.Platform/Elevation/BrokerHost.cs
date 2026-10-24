using System.Globalization;
using Updatr.Core.Diagnostics;
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

        return new BrokerServer(BrokerHandlers.All).RunAsync(args[index + 1], parent, CancellationToken.None).GetAwaiter().GetResult();
    }
}
