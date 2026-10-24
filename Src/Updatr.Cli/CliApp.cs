using System.CommandLine;
using Updatr.Core;
using Updatr.Core.Preflight;
using Updatr.Platform;
using Updatr.Platform.Elevation;

namespace Updatr.Cli;

/// <summary>
/// The command tree. Every command is a thin adapter: parse options, build the host, run a Core
/// operation through a console sink, map the result to an exit code. No logic of its own - that is
/// what keeps the CLI at parity with the GUI.
/// </summary>
public static class CliApp
{
    /// <summary>Exit codes, stable for scripts.</summary>
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int Usage = 2;
        public const int Canceled = 130;
    }

    public static readonly Option<string?> DataDirOption = new("--data-dir")
    {
        Description = "Use this data folder instead of Updatr.data beside the exe.",
        Recursive = true,
    };

    public static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write one JSON object per line instead of text, for scripts.",
        Recursive = true,
    };

    public static readonly Option<bool> YesOption = new("--yes", "-y")
    {
        Description = "Answer yes to every confirmation.",
        Recursive = true,
    };

    public static RootCommand BuildRootCommand()
    {
        RootCommand root = new($"{ProductInfo.Name} - keep Windows machines updated, set up, and clean.");
        root.Options.Add(DataDirOption);
        root.Options.Add(JsonOption);
        root.Options.Add(YesOption);
        root.Subcommands.Add(PreflightCommand());
        root.Subcommands.Add(AdminCommand());
        return root;
    }

    private static Command AdminCommand()
    {
        Command test = new("test", "Request administrator access (one UAC prompt) and confirm the elevated helper works.");

        test.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            ElevationTestResult result = await host.TestElevation.RunAsync(true, sink, cancellationToken).ConfigureAwait(false);
            sink.Result(result.Succeeded, result.Summary);
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failed;
        }));

        Command admin = new("admin", "Administrator access used by machine-wide installs and defluff.");
        admin.Subcommands.Add(test);
        return admin;
    }

    private static Command PreflightCommand()
    {
        Option<string[]> only = new("--only")
        {
            Description = "Run only these checks (and what they depend on), e.g. --only network winget.",
            AllowMultipleArgumentsPerToken = true,
        };

        Option<bool> verbose = new("--verbose", "-v")
        {
            Description = "Show the detail behind every check, not just failures and warnings.",
        };

        Command command = new("preflight", "Check this machine is ready: Windows, network, winget, catalog, tools, admin access, disk.");
        command.Options.Add(only);
        command.Options.Add(verbose);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            string[]? selected = parse.GetValue(only) is { Length: > 0 } ids ? ids : null;
            sink.Verbose = parse.GetValue(verbose);

            PreflightReport report;

            try
            {
                report = await host.Preflight.RunAsync(new PreflightRequest(selected), sink, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                sink.Error(ex.Message);
                return ExitCodes.Usage;
            }

            sink.Summary(report);
            return report.HasFailures ? ExitCodes.Failed : ExitCodes.Ok;
        }));

        return command;
    }

    private static async Task<int> RunAsync(ParseResult parse, CancellationToken cancellationToken, Func<UpdatrHost, ConsoleSink, Task<int>> body)
    {
        ConsoleSink sink = parse.GetValue(JsonOption) ? new JsonLinesSink(parse.GetValue(YesOption)) : new ConsoleSink(parse.GetValue(YesOption));

        await using UpdatrHost host = await UpdatrHost.StartAsync(new HostOptions("cli", parse.GetValue(DataDirOption)), cancellationToken).ConfigureAwait(false);

        try
        {
            return await body(host, sink).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sink.Error("Canceled.");
            return ExitCodes.Canceled;
        }
    }
}
