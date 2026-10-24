using System.Runtime.InteropServices;
using Updatr.Core.Abstractions;
using Updatr.Core.Preflight;
using Updatr.Platform.Data;
using Updatr.Platform.Windows;

namespace Updatr.Platform.Preflight;

/// <summary>Base for checks that need nothing from other checks.</summary>
public abstract class IndependentCheck : IPreflightCheck
{
    public abstract string Id { get; }

    public abstract string Title { get; }

    public IReadOnlyList<string> DependsOn => [];

    public abstract Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken);
}

/// <summary>Windows version and edition. Fails only below the supported minimum.</summary>
public sealed class OsCheck : IndependentCheck
{
    public override string Id => "os";

    public override string Title => "Windows version";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        WindowsInfo.OsDescription os = WindowsInfo.Os();

        PreflightResult result =
            os.Build < WindowsInfo.MinimumBuild
                ? PreflightResult.Fail($"{os} is too old", $"Updatr needs Windows 10 2004 (build {WindowsInfo.MinimumBuild}) or newer.")
            : os.IsLtsc
                ? PreflightResult.Warn($"{os} (LTSC)", "LTSC has no Microsoft Store: winget can be installed, but Store apps (the msstore source) are unavailable.")
            : os.IsServer
                ? PreflightResult.Warn(os.ToString(), "Windows Server: some desktop apps and debloat items do not apply.")
            : PreflightResult.Info(os.ToString());

        return Task.FromResult(result);
    }
}

/// <summary>Machine and process architecture. Warns when an x64 build runs emulated on ARM64.</summary>
public sealed class ArchitectureCheck : IndependentCheck
{
    public override string Id => "architecture";

    public override string Title => "Architecture";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        Architecture os = RuntimeInformation.OSArchitecture;
        Architecture process = RuntimeInformation.ProcessArchitecture;

        PreflightResult result = os == Architecture.Arm64 && process == Architecture.X64
            ? PreflightResult.Warn("x64 build running emulated on ARM64", "Use the win-arm64 release on this machine - it is faster and picks ARM64 installers.")
            : PreflightResult.Info(os.ToString().ToLowerInvariant());

        return Task.FromResult(result);
    }
}

/// <summary>The portable data folder can be written. Warns (does not fail) when it cannot.</summary>
public sealed class DataFolderCheck(IDataStore store) : IndependentCheck
{
    public override string Id => "data";

    public override string Title => "Data folder";

    public override async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        string? fileSystem = AppLocations.FileSystemOf(store.DisplayRoot);
        string where = $"{store.DisplayRoot} ({fileSystem ?? "unknown file system"})";
        string? problem = await store.ProbeWritableAsync(cancellationToken).ConfigureAwait(false);

        return problem is null
            ? PreflightResult.Pass(where)
            : PreflightResult.Warn("Read-only - carts, settings, and logs will not be saved", $"{where}: {problem}");
    }
}

/// <summary>Free space on the system drive, where installers download and install.</summary>
public sealed class DiskSpaceCheck : IndependentCheck
{
    private const long WarnBelow = 10L * 1024 * 1024 * 1024;

    public override string Id => "disk";

    public override string Title => "Disk space";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        DriveInfo drive = new(systemRoot);
        string free = $"{drive.AvailableFreeSpace / (1024.0 * 1024 * 1024):0.0} GB free on {systemRoot.TrimEnd('\\')}";

        return Task.FromResult(drive.AvailableFreeSpace < WarnBelow
            ? PreflightResult.Warn(free, "Large installs (SDKs, games, Visual Studio) may fail. Free up space first.")
            : PreflightResult.Pass(free));
    }
}

/// <summary>Whether admin-only steps (machine installs, debloat) can be elevated.</summary>
public sealed class ElevationCheck : IndependentCheck
{
    public override string Id => "elevation";

    public override string Title => "Administrator access";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        WindowsInfo.Elevation elevation = WindowsInfo.CurrentElevation();
        context.ElevationAvailable = elevation != WindowsInfo.Elevation.StandardUser;

        PreflightResult result = elevation switch
        {
            WindowsInfo.Elevation.AdminFiltered => PreflightResult.Pass("Available (one UAC prompt when first needed)"),
            WindowsInfo.Elevation.AdminUacOff => PreflightResult.Pass("Available (UAC is off)"),
            WindowsInfo.Elevation.Elevated => PreflightResult.Warn(
                "Running as administrator",
                "Start Updatr normally instead: per-user apps installed from an elevated session can land in the wrong profile. Updatr asks for elevation only for the steps that need it."),
            _ => PreflightResult.Warn(
                "This account is not an administrator",
                "Machine-wide installs and debloat need an administrator to approve the UAC prompt."),
        };

        return Task.FromResult(result);
    }
}

/// <summary>Whether Windows is waiting for a reboot, which makes many installers fail.</summary>
public sealed class PendingRebootCheck : IndependentCheck
{
    public override string Id => "reboot";

    public override string Title => "Pending reboot";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> reasons = WindowsInfo.PendingRebootReasons();

        return Task.FromResult(reasons.Count == 0
            ? PreflightResult.Pass("None")
            : PreflightResult.Warn("Windows is waiting for a reboot", string.Join("; ", reasons) + ". Some installs may fail until you reboot."));
    }
}

/// <summary>Whether another installer is running. Updatr would wait for it rather than collide.</summary>
public sealed class InstallerActivityCheck : IndependentCheck
{
    public override string Id => "busy";

    public override string Title => "Other installers";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> busy = WindowsInfo.InstallerActivity();

        return Task.FromResult(busy.Count == 0
            ? PreflightResult.Pass("Idle")
            : PreflightResult.Warn(string.Join("; ", busy), "Updatr will wait for these before starting each install instead of colliding with them."));
    }
}
