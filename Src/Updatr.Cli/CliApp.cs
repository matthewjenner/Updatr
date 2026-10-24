using System.CommandLine;
using Updatr.Core;
using Updatr.Core.Preflight;
using Updatr.Platform;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.WinGet;

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
        root.Subcommands.Add(ListCommand());
        root.Subcommands.Add(UpgradeCommand());
        root.Subcommands.Add(ReinstallCommand());
        root.Subcommands.Add(HoldCommand("hold", "Hold packages: upgrade --all leaves them alone."));
        root.Subcommands.Add(HoldCommand("unhold", "Release holds - yours, and the catalog's on the named packages."));
        root.Subcommands.Add(WinGetCommand());
        root.Subcommands.Add(AdminCommand());
        return root;
    }

    private static Command ListCommand()
    {
        Option<bool> updates = new("--updates", "-u") { Description = "Only packages with an update available." };
        Option<string?> source = new("--source") { Description = "Only this source (winget)." };

        Command command = new("list", "List installed packages, their versions, available updates, and holds.");
        command.Options.Add(updates);
        command.Options.Add(source);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            IReadOnlyList<ListedPackage> packages = await host.List.RunAsync(new ListRequest(parse.GetValue(updates), parse.GetValue(source)), sink, cancellationToken).ConfigureAwait(false);
            sink.Table(packages);
            return packages.Any(p => p.Package.CheckProblem is not null) ? ExitCodes.Failed : ExitCodes.Ok;
        }));

        return command;
    }

    private static Command UpgradeCommand()
    {
        Argument<string[]> ids = new("ids") { Description = "Package ids to upgrade (e.g. Git.Git). Holds do not apply to packages you name.", Arity = ArgumentArity.ZeroOrMore };
        Option<bool> all = new("--all", "-a") { Description = "Upgrade everything with an update, except held packages." };
        Option<bool> includeHeld = new("--include-held") { Description = "With --all: include held packages too." };
        Option<bool> includeUnknown = new("--include-unknown") { Description = "With --all: include packages whose installed version is unknown." };

        Command command = new("upgrade", "Upgrade named packages, or all of them with --all. One installer at a time.");
        command.Arguments.Add(ids);
        command.Options.Add(all);
        command.Options.Add(includeHeld);
        command.Options.Add(includeUnknown);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            string[] named = parse.GetValue(ids) ?? [];
            bool everything = parse.GetValue(all);

            if (named.Length == 0 == !everything)
            {
                sink.Error("Name the packages to upgrade, or pass --all (not both).");
                return ExitCodes.Usage;
            }

            PackageSelection selection = new()
            {
                Ids = everything ? null : named,
                All = everything,
                IncludeHeld = parse.GetValue(includeHeld),
                IncludeUnknown = parse.GetValue(includeUnknown),
            };

            RunSummary summary = await host.Upgrade.RunAsync(selection, sink, cancellationToken).ConfigureAwait(false);
            sink.Summary(summary);
            return summary.HasFailures ? ExitCodes.Failed : ExitCodes.Ok;
        }));

        return command;
    }

    private static Command ReinstallCommand()
    {
        Argument<string[]> ids = new("ids") { Description = "Package ids to reinstall.", Arity = ArgumentArity.OneOrMore };
        Command command = new("reinstall", "Install packages again over themselves, to repair a broken install.");
        command.Arguments.Add(ids);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            RunSummary summary = await host.Reinstall.RunAsync(new PackageSelection { Ids = parse.GetValue(ids) }, sink, cancellationToken).ConfigureAwait(false);
            sink.Summary(summary);
            return summary.HasFailures ? ExitCodes.Failed : ExitCodes.Ok;
        }));

        return command;
    }

    private static Command HoldCommand(string name, string description)
    {
        Argument<string[]> ids = new("ids") { Description = "Package ids.", Arity = ArgumentArity.OneOrMore };
        Option<string> source = new("--source") { Description = "Source the ids belong to.", DefaultValueFactory = _ => "winget" };
        Option<string> reason = new("--reason") { Description = "Why (shown in the list).", DefaultValueFactory = _ => "Held by you" };

        Command command = new(name, description);
        command.Arguments.Add(ids);
        command.Options.Add(source);

        if (name == "hold")
            command.Options.Add(reason);

        command.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            HoldRequest request = new([.. parse.GetValue(ids)!.Select(id => new PackageKey(parse.GetValue(source)!, id))], parse.GetValue(reason) ?? "Held by you");
            int count = name == "hold"
                ? await host.Hold.RunAsync(request, sink, cancellationToken).ConfigureAwait(false)
                : await host.Unhold.RunAsync(request, sink, cancellationToken).ConfigureAwait(false);

            sink.Result(true, $"{(name == "hold" ? "Held" : "Released")} {count} package(s).");
            return ExitCodes.Ok;
        }));

        return command;
    }

    private static Command WinGetCommand()
    {
        Command repair = new("repair", "Install or re-register winget (App Installer) on a machine where it is missing - fresh images, slim ISOs, LTSC.");

        repair.SetAction((parse, cancellationToken) => RunAsync(parse, cancellationToken, async (host, sink) =>
        {
            WinGetRepairResult result = await host.RepairWinGet.RunAsync(true, sink, cancellationToken).ConfigureAwait(false);
            sink.Result(result.Succeeded, result.Summary);
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failed;
        }));

        Command winget = new("winget", "winget itself.");
        winget.Subcommands.Add(repair);
        return winget;
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

        Command admin = new("admin", "Administrator access used by machine-wide installs and debloat.");
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
