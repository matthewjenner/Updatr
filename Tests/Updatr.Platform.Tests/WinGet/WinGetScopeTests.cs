using Updatr.Core.Sources;
using Updatr.Platform.WinGet;

namespace Updatr.Platform.Tests.WinGet;

public sealed class WinGetScopeTests
{
    [Theory]
    [InlineData("System", InstallScope.Machine)] // What winget actually reports for a per-machine MSI (Everything).
    [InlineData("Machine", InstallScope.Machine)]
    [InlineData("user", InstallScope.User)]
    [InlineData("", InstallScope.Unknown)]
    [InlineData(null, InstallScope.Unknown)]
    public void InstalledScopeIsReadInBothSpellings(string? metadata, InstallScope expected)
        => WinGetComSource.ParseScope(metadata).Should().Be(expected);
}
