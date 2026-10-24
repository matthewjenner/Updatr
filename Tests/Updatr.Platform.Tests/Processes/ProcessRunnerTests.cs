using Updatr.Core.Abstractions;
using Updatr.Platform.Processes;

namespace Updatr.Platform.Tests.Processes;

public sealed class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task CapturesOutputAndExitCode()
    {
        ProcessResult result = await Runner.RunAsync(Cmd, ["/d", "/c", "echo hello& exit /b 3"], TimeSpan.FromSeconds(10), Ct);

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Trim().Should().Be("hello");
    }

    [Fact]
    public async Task ATimeoutKillsTheProcessAndSaysSo()
    {
        // ping waits ~1 s between echoes; 20 of them is far past the timeout.
        Func<Task> run = () => Runner.RunAsync(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "20", "127.0.0.1"], TimeSpan.FromMilliseconds(500), Ct);

        (await run.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain("PING.EXE");
    }

    [Fact]
    public void FindsProgramsOnPathWithPathExt()
    {
        Runner.FindOnPath("cmd").Should().EndWithEquivalentOf(@"\cmd.exe");
    }

    [Fact]
    public void AMissingProgramIsNull()
    {
        Runner.FindOnPath($"definitely-not-installed-{Guid.NewGuid():N}").Should().BeNull();
    }
}
