using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

public sealed class WinGetTableParserTests
{
    private static readonly string Ellipsis = WinGetTableParser.Ellipsis.ToString();

    private static readonly string Output = string.Join("\r\n",
        "   - \\ |",
        "Name                          Id                              Version       Available     Source",
        "--------------------------------------------------------------------------------------------------",
        "Microsoft Visual Studio Code  Microsoft.VisualStudioCode      1.136.1       1.142.0       winget",
        "Zoom Workplace                Zoom.Zoom.EXE                   7.1.9 (48550) 7.2.1 (48556) winget",
        $"Some Very Long Product Name   Publisher.AVeryLongPackageId{Ellipsis}   1.0           2.0           winget",
        "Battle.net                    Blizzard.BattleNet              Unknown       2.0.0.4232    winget",
        "4 upgrades available.",
        "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.");

    [Fact]
    public void ParsesColumnsByHeaderPosition()
    {
        IReadOnlyList<WinGetTableParser.Row> rows = WinGetTableParser.Parse(Output)!;

        rows.Should().HaveCount(4);
        rows[0].Should().Be(new WinGetTableParser.Row("Microsoft Visual Studio Code", "Microsoft.VisualStudioCode", "1.136.1", "1.142.0", "winget", false));
    }

    [Fact]
    public void VersionsWithSpacesSurvive()
    {
        WinGetTableParser.Row zoom = WinGetTableParser.Parse(Output)!.Single(r => r.Id == "Zoom.Zoom.EXE");

        zoom.Version.Should().Be("7.1.9 (48550)");
        zoom.Available.Should().Be("7.2.1 (48556)");
    }

    [Fact]
    public void TruncatedIdsAreFlagged()
    {
        WinGetTableParser.Parse(Output)!.Single(r => r.Id.StartsWith("Publisher.", StringComparison.Ordinal)).IdTruncated.Should().BeTrue();
    }

    [Fact]
    public void FootersAreNotRows()
    {
        WinGetTableParser.Parse(Output)!.Should().NotContain(r => r.Name.Contains("upgrades available", StringComparison.Ordinal));
    }

    [Fact]
    public void NoHeaderMeansNull()
    {
        // A non-English UI, or an error message instead of a table.
        WinGetTableParser.Parse("Nom    Id    Version\n-----\nAucun paquet").Should().BeNull();
        WinGetTableParser.Parse("An unexpected error occurred while executing the command:\n0x80070057").Should().BeNull();
    }
}
