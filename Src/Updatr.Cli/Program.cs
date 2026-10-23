using System.CommandLine;
using Updatr.Core;

namespace Updatr.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        RootCommand root = new($"{ProductInfo.Name} - keep Windows machines updated, set up, and clean.");
        // Phase 0: commands arrive with the operations they wrap (doctor in Phase 2).
        root.SetAction(_ =>
        {
            Console.WriteLine($"{ProductInfo.Name} CLI v{ProductInfo.Version}. Run with --help for usage.");
            return 0;
        });

        return root.Parse(args).Invoke();
    }
}
