using System.Text.RegularExpressions;

namespace Updatr.Core.Sources;

/// <summary>What sort of thing an installed package is - for sorting a 400-row list into something readable.</summary>
public enum AppKind
{
    App,
    Game,
    Runtime,
    Driver,
    Windows,
    Developer,
}

/// <summary>
/// Classifies installed packages by name, id, and source. First match wins, in this order:
/// developer packages (npm, pipx, dotnet tools, PSGallery, VS Code), games (anything a game launcher
/// manages, and the launchers), runtimes and SDKs, drivers and hardware tools, Windows components,
/// and everything else is an app.
/// </summary>
/// <remarks>
/// Rules were fitted to a real 460-package machine (2026-10-25). A wrong guess only changes which
/// filter shows a row; it never changes what Updatr does with it.
/// </remarks>
public static partial class AppKinds
{
    /// <summary>Label for the filter chips and the Kind column.</summary>
    public static string Label(AppKind kind) => kind switch
    {
        AppKind.Game => "Games",
        AppKind.Runtime => "Runtimes & SDKs",
        AppKind.Driver => "Drivers & hardware",
        AppKind.Windows => "Windows components",
        AppKind.Developer => "Developer packages",
        _ => "Apps",
    };

    /// <summary>The CLI's word for each kind (<c>list --kind games</c>).</summary>
    public static string CliName(AppKind kind) => kind switch
    {
        AppKind.Game => "games",
        AppKind.Runtime => "runtimes",
        AppKind.Driver => "drivers",
        AppKind.Windows => "windows",
        AppKind.Developer => "developer",
        _ => "apps",
    };

    public static AppKind? FromCliName(string name)
        => Enum.GetValues<AppKind>().Cast<AppKind?>().FirstOrDefault(k => string.Equals(CliName(k!.Value), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Game launchers' winget ids and names. Launchers count as games: that is where people look for them.</summary>
    private static readonly HashSet<string> LauncherIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Valve.Steam", "Blizzard.BattleNet", "EpicGames.EpicGamesLauncher", "EpicGames.EpicOnlineServices", "Ubisoft.Connect",
        "ElectronicArts.EADesktop", "GOG.Galaxy", "CloudImperiumGames.RSILauncher", "ParadoxInteractive.ParadoxLauncher",
        "ItchIo.Itch", "HypixelStudios.Hytale", "RiotGames.RiotClient", "Microsoft.GamingApp",
    };

    [GeneratedRegex(@"\b(Games? Launcher|Studios Launcher|Steam App \d+)\b|^XBOX$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameName();

    [GeneratedRegex(@"(\bRuntime\b|Redistributable|\bRedist\b|\bFramework\b|\bSDK\b|Software Development Kit|\bJDK\b|\bJRE\b|Visual C\+\+|\bVCLibs\b|\B\.NET\b|CUDA Toolkit|DirectX|\bPhysX\b|\bGameInput\b|UI\.Xaml|WinAppRuntime|WindowsAppRuntime|App SDK|CLR Types|ODBC Driver|OLE DB Driver|^Python (3|Launcher)|Debuggers And Tools|Targeting Pack|Help Viewer|vs_CoreEditorFonts|Level Zero|Native Framework)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeName();

    [GeneratedRegex(@"(\bDrivers?\b|Chipset|Graphics Software|Management Engine|Bluetooth|Audio Control|USB Audio|NVIDIA Control Panel|\bHOTAS\b|H\.O\.T\.A\.S|TrackIR|\bTobii\b|MSI Center|\bG HUB\b|Stream Deck|Wave Link|PlayStation Accessories|libusb|Print( and Scan)? Experience|DirectOutput|CPU-Z|HWiNFO|Extreme Tuning|Processor Diagnostic|Driver & Support|Computing Improvement|CrystalDiskInfo|Garmin (Express|WebUpdater))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverName();

    [GeneratedRegex(@"^(Windows (?!App$|Subsystem|Terminal|Software Development)|Microsoft (Store|Update Health|Edge|Engagement|Defender|Photos|Remote Desktop|Ink|Teams Meeting Add-in|Windows Application)|Microsoft \w+ ?\(Natural\)|Store Experience Host|Start Experiences App|Gaming Services|Game Bar|Game Speech Window|Xbox |Mobile devices|Narrator|Speech Pack|Quick Assist|Paint$|Snipping Tool|App Installer|Ink\.Handwriting|Widgets Platform Runtime|OneDrive$|Microsoft OneDrive|OfficePushNotificationsUtility|Microsoft\.Office\.|Local AI Manager)|(Video|Image) Extensions?( from Device Manufacturer)?$|^Web Media Extensions", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsName();

    public static AppKind Classify(InstalledPackage package)
    {
        if (package.Source != "winget")
            return AppKind.Developer;

        if (package.ManagedBy is not null || LauncherIds.Contains(package.Id) || GameName().IsMatch(package.Name) || GameName().IsMatch(package.Id))
            return AppKind.Game;

        if (RuntimeName().IsMatch(package.Name))
            return AppKind.Runtime;

        if (DriverName().IsMatch(package.Name))
            return AppKind.Driver;

        // Windows' own packages: by name, or by the package identity Microsoft ships inbox apps under.
        if (WindowsName().IsMatch(package.Name) || package.Id.StartsWith(@"MSIX\MicrosoftWindows.", StringComparison.OrdinalIgnoreCase))
            return AppKind.Windows;

        return AppKind.App;
    }

    /// <summary>Where a package comes from, for the Source filter.</summary>
    public static string SourceLabel(InstalledPackage package) => package switch
    {
        { ManagedBy: not null } => "Game launchers",
        { Source: "winget", Catalog: "msstore" } => "Microsoft Store",
        { Source: "winget", IsManaged: true } => "winget",
        { Source: "winget" } => "Not in a catalog",
        { Source: "npm" } => "npm",
        { Source: "pipx" } => "pipx",
        { Source: "dotnetTool" } => "dotnet tool",
        { Source: "psGallery" } => "PSGallery",
        { Source: "vscode" } => "VS Code",
        _ => package.Source,
    };
}
