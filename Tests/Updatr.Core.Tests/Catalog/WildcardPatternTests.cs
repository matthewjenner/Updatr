using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

public sealed class WildcardPatternTests
{
    [Theory]
    [InlineData("OpenJS.NodeJS*", "OpenJS.NodeJS", true)]
    [InlineData("OpenJS.NodeJS*", "OpenJS.NodeJS.LTS", true)]
    [InlineData("OpenJS.NodeJS*", "openjs.nodejs.lts", true)]
    [InlineData("OpenJS.NodeJS*", "X.OpenJS.NodeJS", false)]
    [InlineData("Gradle.Gradle", "Gradle.Gradle", true)]
    [InlineData("Gradle.Gradle", "Gradle.Gradle.Extra", false)]
    [InlineData("Python.Python.3.?", "Python.Python.3.9", true)]
    [InlineData("Python.Python.3.?", "Python.Python.3.12", false)]
    public void MatchesWholeIdsCaseInsensitively(string pattern, string value, bool expected)
    {
        new WildcardPattern(pattern).IsMatch(value).Should().Be(expected);
    }

    [Fact]
    public void DotsAndOtherRegexCharactersAreLiteral()
    {
        WildcardPattern pattern = new("Microsoft.VCRedist.*");

        pattern.IsMatch("Microsoft.VCRedist.2015+.x64").Should().BeTrue();
        pattern.IsMatch("MicrosoftXVCRedist.2015").Should().BeFalse();
    }
}
