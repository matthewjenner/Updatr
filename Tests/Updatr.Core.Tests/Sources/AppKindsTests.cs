using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

/// <summary>Names and ids are real entries from a 460-package machine.</summary>
public sealed class AppKindsTests
{
    private static InstalledPackage Pkg(string name, string id = "Some.Id", string source = "winget", string? managedBy = null, string? catalog = "winget", bool managed = true)
        => new() { Source = source, Id = id, Name = name, InstalledVersion = "1.0", ManagedBy = managedBy, Catalog = catalog, IsManaged = managed };

    [Theory]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64)", AppKind.Runtime)]
    [InlineData("Microsoft .NET Windows Desktop Runtime 8.0", AppKind.Runtime)]
    [InlineData("Eclipse Temurin JDK with Hotspot 21", AppKind.Runtime)]
    [InlineData("Windows Software Development Kit - Windows 10.0.26100.4188", AppKind.Runtime)]
    [InlineData("Python 3.13", AppKind.Runtime)]
    [InlineData("NVIDIA PhysX System Software", AppKind.Runtime)]
    [InlineData("Microsoft ODBC Driver 17 for SQL Server", AppKind.Runtime)]
    [InlineData("paint.net", AppKind.App)]
    [InlineData("Intel(R) Chipset Device Software", AppKind.Driver)]
    [InlineData("NVIDIA Graphics Driver 616.56", AppKind.Driver)]
    [InlineData("Logitech G HUB", AppKind.Driver)]
    [InlineData("X56 H.O.T.A.S.", AppKind.Driver)]
    [InlineData("Windows Calculator", AppKind.Windows)]
    [InlineData("HEVC Video Extensions from Device Manufacturer", AppKind.Windows)]
    [InlineData("Microsoft Jenny(Natural) - English (United States)", AppKind.Windows)]
    [InlineData("App Installer", AppKind.Windows)]
    [InlineData("Windows Terminal", AppKind.App)]
    [InlineData("Windows Subsystem for Linux", AppKind.App)]
    [InlineData("Windows App", AppKind.App)]
    [InlineData("Mozilla Firefox (en-US)", AppKind.App)]
    [InlineData("Battlestate Games Launcher 15.1.0.4635", AppKind.Game)]
    [InlineData("XBOX", AppKind.Game)]
    public void NamesAreSortedIntoKinds(string name, AppKind expected) => AppKinds.Classify(Pkg(name)).Should().Be(expected);

    [Fact]
    public void AnythingALauncherManagesIsAGameWhateverItIsCalled()
        => AppKinds.Classify(Pkg("Microsoft Flight Simulator 2024", @"ARP\Machine\X64\Steam App 2537590", managedBy: "Steam")).Should().Be(AppKind.Game);

    [Fact]
    public void LaunchersThemselvesAreGames()
        => AppKinds.Classify(Pkg("Steam", "Valve.Steam")).Should().Be(AppKind.Game);

    [Fact]
    public void EveryOtherEcosystemIsDeveloperPackages()
        => new[] { "npm", "pipx", "dotnetTool", "psGallery", "vscode" }
            .Select(s => AppKinds.Classify(Pkg("Microsoft .NET Runtime lookalike", source: s))).Should().OnlyContain(k => k == AppKind.Developer);

    [Theory]
    [InlineData("winget", "msstore", true, null, "Microsoft Store")]
    [InlineData("winget", "winget", true, null, "winget")]
    [InlineData("winget", null, false, null, "Not in a catalog")]
    [InlineData("winget", null, false, "Steam", "Game launchers")]
    [InlineData("vscode", "VS Code", true, null, "VS Code")]
    public void SourcesHaveFilterLabels(string source, string? catalog, bool managed, string? launcher, string expected)
        => AppKinds.SourceLabel(Pkg("x", source: source, catalog: catalog, managed: managed, managedBy: launcher)).Should().Be(expected);

    [Fact]
    public void CliNamesRoundTrip()
        => Enum.GetValues<AppKind>().Select(k => AppKinds.FromCliName(AppKinds.CliName(k))).Should().Equal(Enum.GetValues<AppKind>().Cast<AppKind?>());
}
