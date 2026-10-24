using System.Text;
using Updatr.Platform.Elevation;

namespace Updatr.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Launched elevated as the broker for another Updatr process: serve it and exit.
        if (BrokerHost.TryRun(args) is int brokerExit)
            return brokerExit;

        Console.OutputEncoding = Encoding.UTF8;
        return await CliApp.BuildRootCommand().Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
