using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

/// <summary>Patterns taken from real uninstall entries on the dev machine (Phase 3).</summary>
public sealed class LauncherDetectorTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/730", null, "Steam")]
    [InlineData(null, @"D:\SteamLibrary\steamapps\common\Hades II", "Steam")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=wow --displayname=\"World of Warcraft\"", null, "Battle.net")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=fenris --displayname=\"Diablo IV\"", null, "Battle.net")]
    [InlineData("\"C:\\Program Files (x86)\\Epic Games\\Launcher\\Portal\\Binaries\\Win32\\EpicGamesLauncher.exe\" com.epicgames.launcher://apps/abc?action=uninstall", null, null)]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=uninstall", null, "Epic Games")]
    [InlineData("\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\upc.exe\" uplay://uninstall/5595", null, "Ubisoft Connect")]
    [InlineData(null, @"C:\Program Files (x86)\GOG Galaxy\Games\Cyberpunk 2077", "GOG GALAXY")]
    public void GamesAreAttributedToTheirLauncher(string? uninstall, string? location, string? expected)
    {
        LauncherDetector.ManagedBy(uninstall, location).Should().Be(expected);
    }

    [Theory]
    [InlineData("C:\\Program Files (x86)\\Steam\\uninstall.exe")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=battle.net --displayname=\"Battle.net\"")]
    [InlineData("C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\Uninstall.exe")]
    public void LaunchersThemselvesAreNotLauncherManaged(string uninstall)
    {
        // A launcher is a self-updating app (an explicit hold in the catalog), not owned by itself.
        LauncherDetector.ManagedBy(uninstall, null).Should().BeNull();
    }

    [Fact]
    public void TheEpicLauncherIsNotManagedByItself()
    {
        // Seen on the dev machine: the Epic launcher is an MSI installed at the root its games go under.
        LauncherDetector.ManagedBy("MsiExec.exe /X{A2FB1E1A-55D9-4511-A0BF-DEAD0493FBBC}", @"C:\Program Files (x86)\Epic Games\", "EpicGames.EpicGamesLauncher").Should().BeNull();
        LauncherDetector.ManagedBy("MsiExec.exe /X{A2FB1E1A-55D9-4511-A0BF-DEAD0493FBBC}", @"C:\Program Files (x86)\Epic Games\").Should().BeNull();
        LauncherDetector.ManagedBy(null, @"C:\Program Files (x86)\Epic Games\Launcher").Should().BeNull();
        LauncherDetector.ManagedBy(null, @"C:\Program Files\Epic Games\Fortnite").Should().Be("Epic Games");
    }

    [Fact]
    public void BattlestateGamesAreRecognizedByPublisherWhereverTheyAreInstalled()
    {
        // Real entries (2026-10-25): each game has its own Uninstall.exe in a folder the user chose.
        LauncherDetector.ManagedBy(@"E:\games\Escape from Tarkov\Uninstall.exe", @"E:\games\Escape from Tarkov", null, "Battlestate Games", "Escape from Tarkov")
            .Should().Be("Battlestate Games Launcher");
        LauncherDetector.ManagedBy(@"E:\games\Escape from Tarkov Arena\Uninstall.exe", @"E:\games\Escape from Tarkov Arena", null, "Battlestate Games", "Escape from Tarkov: Arena")
            .Should().Be("Battlestate Games Launcher");

        // The launcher itself is not managed by itself.
        LauncherDetector.ManagedBy("\"E:\\games\\BsgLauncher\\unins000.exe\"", @"E:\games\BsgLauncher\", null, "Battlestate Games", "Battlestate Games Launcher 15.1.0.4635")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Git\\unins000.exe\"", @"C:\Program Files\Git")]
    [InlineData("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", null)]
    [InlineData(null, null)]
    public void OrdinaryAppsAreNotLauncherManaged(string? uninstall, string? location)
    {
        LauncherDetector.ManagedBy(uninstall, location).Should().BeNull();
    }
}
