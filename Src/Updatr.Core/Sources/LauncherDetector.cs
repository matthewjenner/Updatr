namespace Updatr.Core.Sources;

/// <summary>
/// Recognizes apps owned by a game launcher (Steam, Battle.net, Epic, ...) from how they register
/// their uninstaller and where they live. Those are updated by the launcher; updating them any
/// other way risks a second copy outside the launcher or a corrupted install, so Updatr never
/// upgrades them through winget.
/// </summary>
/// <remarks>
/// Patterns come from real uninstall entries (Steam: <c>steam.exe steam://uninstall/&lt;appid&gt;</c>;
/// Battle.net: <c>Blizzard Uninstaller.exe --uid=&lt;game&gt;</c>) and the launchers' documented URL
/// schemes. The launchers' own entries (Steam's <c>uninstall.exe</c>, Battle.net's
/// <c>--uid=battle.net</c>) are deliberately not matched: a launcher is not managed by itself - it
/// is a self-updating app, handled by an <c>explicit</c> hold rule in the catalog.
/// </remarks>
public static class LauncherDetector
{
    private static readonly (string Launcher, string[] UninstallMarkers, string[] LocationMarkers)[] Launchers =
    [
        ("Steam", ["steam://uninstall/"], [@"\steamapps\common\"]),
        ("Battle.net", [@"\Battle.net\Agent\Blizzard Uninstaller.exe"], []),
        ("Epic Games", ["com.epicgames.launcher://"], [@"\Epic Games\"]),
        ("Ubisoft Connect", ["uplay://uninstall/"], [@"\Ubisoft Game Launcher\games\"]),
        ("EA app", [@"\EA Desktop\", "EAInstaller"], [@"\EA Games\"]),
        ("GOG GALAXY", [], [@"\GOG Galaxy\Games\"]),
        ("Riot Client", ["RiotClientServices.exe", "--uninstall-product"], [@"\Riot Games\"]),
        ("Rockstar Games Launcher", [@"\Rockstar Games\Launcher\"], []),
    ];

    /// <summary>Launchers' own uninstall entries, which must not be classed as launcher-managed.</summary>
    private static readonly string[] LauncherSelf =
    [
        "--uid=battle.net",
        @"\Steam\uninstall.exe",
        @"\Ubisoft Game Launcher\Uninstall.exe",
        "EpicGamesLauncher",
        "--uninstall-product=riot_client",
    ];

    /// <summary>The launchers' own winget ids: a launcher is never "managed by" itself.</summary>
    private static readonly HashSet<string> LauncherIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Valve.Steam",
        "Blizzard.BattleNet",
        "EpicGames.EpicGamesLauncher",
        "Ubisoft.Connect",
        "ElectronicArts.EADesktop",
        "GOG.Galaxy",
    };

    /// <summary>
    /// Launchers' own install folders. The Epic launcher installs at the root of the folder its games
    /// go under, so the root itself and its Launcher subfolder are the launcher, not a game.
    /// </summary>
    private static readonly string[] LauncherSelfLocationSuffixes = [@"\Epic Games\", @"\Epic Games\Launcher\"];

    /// <summary>The launcher that owns this install, or null when it is not launcher-managed.</summary>
    /// <param name="uninstallCommand">The registered uninstall command, if known.</param>
    /// <param name="installLocation">The install folder, if known.</param>
    /// <param name="packageId">The package's id, when a catalog knows it. A launcher's own id is never launcher-managed.</param>
    public static string? ManagedBy(string? uninstallCommand, string? installLocation, string? packageId = null)
    {
        string uninstall = uninstallCommand ?? string.Empty;
        string location = installLocation is null ? string.Empty : installLocation.TrimEnd('\\') + "\\";

        if (packageId is not null && LauncherIds.Contains(packageId))
            return null;

        if (LauncherSelf.Any(self => uninstall.Contains(self, StringComparison.OrdinalIgnoreCase))
            || LauncherSelfLocationSuffixes.Any(self => location.EndsWith(self, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach ((string launcher, string[] uninstallMarkers, string[] locationMarkers) in Launchers)
        {
            if (uninstallMarkers.Any(m => uninstall.Contains(m, StringComparison.OrdinalIgnoreCase))
                || locationMarkers.Any(m => location.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return launcher;
            }
        }

        return null;
    }
}
