using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;

namespace Updatr.Platform.Debloat;

/// <summary>
/// Service start types through the Service Control Manager. Reading comes from the service's
/// registry key (allowed unelevated); changing goes through the SCM as administrator.
/// </summary>
public sealed partial class ServiceTarget(ElevationService elevation) : IDebloatTarget
{
    private const int Automatic = 2;
    private const int Manual = 3;
    private const int Disabled = 4;

    public bool Handles(DebloatAction action) => action is ServiceAction;

    public bool NeedsAdmin(DebloatAction action) => true;

    public Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        ServiceAction s = (ServiceAction)action;

        if (ReadStart(s.Name) is not { } start)
            return Task.FromResult(new ActionStatus(ActionState.NotApplicable, "Not on this edition of Windows"));

        // Disabled already satisfies "manual": both mean it does not start on its own.
        bool applied = start == Disabled || (s.StartType == ServiceStartType.Manual && start == Manual);
        return Task.FromResult(applied ? new ActionStatus(ActionState.Applied) : new ActionStatus(ActionState.NotApplied, $"Starts {Describe(start)}"));
    }

    public Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken)
    {
        ServiceAction s = (ServiceAction)action;
        int? start = ReadStart(s.Name);

        return Task.FromResult(new ActionBackup
        {
            Action = s,
            Existed = start is not null,
            Value = start?.ToString(CultureInfo.InvariantCulture),
            ValueKind = start == Automatic && IsDelayed(s.Name) ? "delayed" : null,
        });
    }

    public Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => DebloatElevation.ApplyAsync(elevation, action, () => Task.FromResult(ApplyLocal((ServiceAction)action)));

    public Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => backup.Existed
            ? DebloatElevation.RevertAsync(elevation, backup, () => Task.FromResult(RevertLocal((ServiceAction)backup.Action, backup)))
            : Task.FromResult(new PackageOutcome(OutcomeKind.AlreadyCurrent, "The service did not exist"));

    internal static PackageOutcome ApplyLocal(ServiceAction action)
    {
        int start = action.StartType == ServiceStartType.Disabled ? Disabled : Manual;
        PackageOutcome outcome = Change(action.Name, start, delayed: null);

        if (outcome.Kind == OutcomeKind.Ok && start == Disabled)
            Stop(action.Name);

        return outcome;
    }

    internal static PackageOutcome RevertLocal(ServiceAction action, ActionBackup backup)
    {
        // Only the start types a debloat rule could have replaced; never boot / system (drivers).
        if (!int.TryParse(backup.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || start is not (Automatic or Manual or Disabled))
            return PackageOutcome.Failed($"The backup's start type '{backup.Value}' is not one Updatr restores");

        return Change(action.Name, start, delayed: start == Automatic ? backup.ValueKind == "delayed" : null);
    }

    private static int? ReadStart(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("Start") as int?;
    }

    private static bool IsDelayed(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("DelayedAutostart") is 1;
    }

    private static string Describe(int start) => start switch
    {
        Automatic => "automatically",
        Manual => "on demand",
        Disabled => "never (disabled)",
        _ => $"as type {start}",
    };

    private static PackageOutcome Change(string name, int start, bool? delayed)
    {
        using ServiceHandle manager = new(OpenSCManagerW(null, null, ScManagerConnect));

        if (manager.IsInvalid)
            return Win32Failure($"Could not open the service manager");

        using ServiceHandle service = new(OpenServiceW(manager.Handle, name, ServiceChangeConfig | ServiceStop | ServiceQueryStatus));

        if (service.IsInvalid)
            return Win32Failure($"Could not open service {name}");

        if (!ChangeServiceConfigW(service.Handle, ServiceNoChange, (uint)start, ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
            return Win32Failure($"Could not change service {name}");

        if (delayed is { } value)
        {
            int flag = value ? 1 : 0;

            if (!ChangeServiceConfig2W(service.Handle, ServiceConfigDelayedAutoStartInfo, ref flag))
                return Win32Failure($"Could not set delayed start on {name}");
        }

        return PackageOutcome.Ok();
    }

    /// <summary>Best effort: the start type is what matters; a running service stops at the next restart anyway.</summary>
    private static void Stop(string name)
    {
        using ServiceHandle manager = new(OpenSCManagerW(null, null, ScManagerConnect));
        using ServiceHandle service = new(manager.IsInvalid ? IntPtr.Zero : OpenServiceW(manager.Handle, name, ServiceStop));

        if (!service.IsInvalid)
            _ = ControlService(service.Handle, ServiceControlStop, out _);
    }

    private static PackageOutcome Win32Failure(string what)
    {
        int error = Marshal.GetLastPInvokeError();
        return PackageOutcome.Failed($"{what}: {new Win32Exception(error).Message}");
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceControlStop = 1;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;

    private sealed class ServiceHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle => handle;

        public bool IsInvalid => handle == IntPtr.Zero;

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
                CloseServiceHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref int info);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
