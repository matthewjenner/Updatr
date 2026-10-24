using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("/S", "/S")]
    [InlineData("  /VERYSILENT   /NORESTART ", "/VERYSILENT|/NORESTART")]
    [InlineData("/DIR=\"C:\\Program Files\\App\" /S", "/DIR=C:\\Program Files\\App|/S")]
    [InlineData("\"a b\" c", "a b|c")]
    [InlineData("say \\\"hi\\\"", "say|\"hi\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SplitsLikeAWindowsProgram(string? input, string expected)
    {
        string.Join("|", CommandLine.Split(input)).Should().Be(expected);
    }
}
