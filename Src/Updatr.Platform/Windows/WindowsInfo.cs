using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Updatr.Platform.Windows;

/// <summary>Facts about this Windows install, read without running anything.</summary>
public static partial class WindowsInfo
{
    /// <summary>Windows 10 2004. Older builds lack the WinGet COM server and are out of support.</summary>
    public const int MinimumBuild = 19041;

    /// <summary>The OS as the user would name it.</summary>
    public sealed record OsDescription(string Name, string Edition, string? DisplayVersion, int Build, int Revision, bool IsLtsc, bool IsServer)
    {
        public override string ToString()
            => $"{Name} {Edition}{(DisplayVersion is null ? "" : " " + DisplayVersion)} (build {Build}.{Revision})";
    }

    public static OsDescription Os()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        string edition = key?.GetValue("EditionID") as string ?? "Unknown";
        string installation = key?.GetValue("InstallationType") as string ?? "Client";
        int build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out int b) ? b : Environment.OSVersion.Version.Build;
        int revision = key?.GetValue("UBR") is int ubr ? ubr : 0;
        bool server = installation.StartsWith("Server", StringComparison.OrdinalIgnoreCase);

        // ProductName still says "Windows 10" on Windows 11; the build number is the truth.
        string name = server ? "Windows Server" : build >= 22000 ? "Windows 11" : "Windows 10";

        // LTSC editions carry an "S" suffix: EnterpriseS, IoTEnterpriseS, EnterpriseSN.
        bool ltsc = edition.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase);

        return new OsDescription(name, edition, key?.GetValue("DisplayVersion") as string, build, revision, ltsc, server);
    }

    /// <summary>How this process relates to admin rights.</summary>
    public enum Elevation
    {
        /// <summary>An admin running filtered by UAC - elevation is one consent prompt away.</summary>
        AdminFiltered,

        /// <summary>Already elevated.</summary>
        Elevated,

        /// <summary>Admin with UAC off: already has full rights.</summary>
        AdminUacOff,

        /// <summary>Not an admin: elevation needs another account's credentials.</summary>
        StandardUser,
    }

    public static Elevation CurrentElevation()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        int type = 0;

        if (GetTokenInformation(identity.Token, TokenElevationTypeClass, out type, sizeof(int), out _))
        {
            if (type == TokenElevationTypeFull)
                return Elevation.Elevated;
            if (type == TokenElevationTypeLimited)
                return Elevation.AdminFiltered;
        }

        // TokenElevationTypeDefault: no split token - UAC is off, or this is a standard user.
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? Elevation.AdminUacOff : Elevation.StandardUser;
    }

    public static bool IsElevated => CurrentElevation() is Elevation.Elevated or Elevation.AdminUacOff;

    /// <summary>
    /// Reasons a reboot is pending, empty when none. Only the servicing and Windows Update flags:
    /// <c>PendingFileRenameOperations</c> is set by many ordinary installers and would cry wolf.
    /// </summary>
    public static IReadOnlyList<string> PendingRebootReasons()
    {
        List<string> reasons = [];

        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Windows servicing is waiting for a reboot");
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update is waiting for a reboot");

        return reasons;
    }

    /// <summary>Installers or servicing running right now, empty when idle.</summary>
    public static IReadOnlyList<string> InstallerActivity()
    {
        List<string> busy = [];

        // Held only during an actual MSI install transaction - unlike the msiexec process, which
        // runs for many unrelated reasons (Docs/legacy-script.md).
        if (MutexExists(@"Global\_MSIExecute"))
            busy.Add("an MSI install is in progress");

        if (Running("winget"))
            busy.Add("winget is running");

        if (Running("TiWorker"))
            busy.Add("Windows is installing updates (TiWorker)");

        return busy;
    }

    private static bool KeyExists(string path)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }

    private static bool MutexExists(string name)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out Mutex? mutex))
            {
                mutex.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Exists, owned by another session or a service.
        }
    }

    private static bool Running(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);

        foreach (Process p in processes)
            p.Dispose();

        return processes.Length > 0;
    }

    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returnLength);
}
