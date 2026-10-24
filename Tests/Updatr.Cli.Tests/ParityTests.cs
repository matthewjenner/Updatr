using System.CommandLine;
using System.Reflection;
using Updatr.Core.Operations;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.Cli.Tests;

/// <summary>
/// Every user-facing operation is reachable from the CLI. The GUI and the CLI both adapt the same
/// Core operations; this is the test that stops one front end quietly gaining a feature the other
/// lacks.
/// </summary>
public sealed class ParityTests
{
    /// <summary>Every [Operation] type in the assemblies that hold operations.</summary>
    public static TheoryData<string, string> Operations()
    {
        TheoryData<string, string> data = [];

        foreach (Assembly assembly in new[] { typeof(RunPreflight).Assembly, typeof(UpdatrHost).Assembly })
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (type.GetCustomAttribute<OperationAttribute>() is { } attribute)
                    data.Add(type.Name, attribute.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void EveryOperationHasACliCommand(string operationType, string commandPath)
    {
        Command current = CliApp.BuildRootCommand();

        foreach (string segment in commandPath.Split(' '))
        {
            Command? next = current.Subcommands.FirstOrDefault(c => c.Name == segment);
            next.Should().NotBeNull($"{operationType} is [Operation(\"{commandPath}\")] but the CLI has no '{commandPath}' command");
            current = next!;
        }

        current.Action.Should().NotBeNull($"'{commandPath}' must run something");
    }

    [Fact]
    public void ThereIsAtLeastOneOperation()
    {
        // Guards the theory above: a reflection mistake that finds nothing would pass vacuously.
        Operations().Should().NotBeEmpty();
    }
}

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("preflight")]
    [InlineData("preflight --json")]
    [InlineData("preflight --only network winget --verbose")]
    [InlineData("preflight --data-dir D:\\Updatr.data --yes")]
    public void ValidCommandLinesParse(string commandLine)
    {
        CliApp.BuildRootCommand().Parse(commandLine).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("doctor")]
    [InlineData("preflight --bogus")]
    public void InvalidCommandLinesAreRejected(string commandLine)
    {
        CliApp.BuildRootCommand().Parse(commandLine).Errors.Should().NotBeEmpty();
    }
}
