using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Updatr.Platform.Leftovers;

/// <summary>A service's definition, enough to create it again.</summary>
public sealed record ServiceDefinition(
    string Name, string? DisplayName, string? Description, string ImagePath, int Type, int Start, int ErrorControl,
    string? Account, IReadOnlyList<string> Dependencies, bool Delayed);

/// <summary>Reads, deletes, and recreates services (administrator only for the last two).</summary>
public static partial class ServiceControl
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Accounts a service can be recreated with; others need a password Updatr does not have.</summary>
    private static readonly HashSet<string> BuiltInAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalSystem", @"NT AUTHORITY\LocalService", @"NT AUTHORITY\NetworkService", @"NT AUTHORITY\LOCAL SERVICE", @"NT AUTHORITY\NETWORK SERVICE",
    };

    public static ServiceDefinition? Read(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{name}");

        if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string image)
            return null;

        return new ServiceDefinition(
            name,
            key.GetValue("DisplayName") as string,
            key.GetValue("Description") as string,
            image,
            key.GetValue("Type") as int? ?? 0x10,
            key.GetValue("Start") as int? ?? 3,
            key.GetValue("ErrorControl") as int? ?? 1,
            key.GetValue("ObjectName") as string,
            key.GetValue("DependOnService") as string[] ?? [],
            key.GetValue("DelayedAutostart") is 1);
    }

    /// <summary>Stops (best effort) and deletes the service.</summary>
    public static void Delete(string name)
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);

        if (manager == IntPtr.Zero)
            throw Last("Could not open the service manager");

        try
        {
            IntPtr service = OpenServiceW(manager, name, Delete_ | ServiceStop);

            if (service == IntPtr.Zero)
                throw Last($"Could not open service {name}");

            try
            {
                _ = ControlService(service, ServiceControlStop, out _);

                if (!DeleteService(service))
                    throw Last($"Could not delete service {name}");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Creates the service from its saved definition. Returns why not, or null.</summary>
    public static string? Create(ServiceDefinition definition)
    {
        if (definition.Account is { } account && !BuiltInAccounts.Contains(account))
            return $"service {definition.Name} ran as {account}; recreate it by reinstalling the app (Updatr does not have the password)";

        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect | ScManagerCreateService);

        if (manager == IntPtr.Zero)
            return Last("Could not open the service manager").Message;

        try
        {
            // Dependencies are a double-null-terminated list.
            string? dependencies = definition.Dependencies.Count == 0 ? null : string.Join('\0', definition.Dependencies) + "\0";
            IntPtr service = CreateServiceW(manager, definition.Name, definition.DisplayName ?? definition.Name, ServiceAllAccess,
                (uint)definition.Type, (uint)definition.Start, (uint)definition.ErrorControl, definition.ImagePath, null, IntPtr.Zero, dependencies,
                definition.Account, null);

            if (service == IntPtr.Zero)
                return Last($"Could not create service {definition.Name}").Message;

            if (definition.Delayed)
            {
                int flag = 1;
                _ = ChangeServiceConfig2W(service, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, ref flag);
            }

            CloseServiceHandle(service);

            if (definition.Description is { } description)
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{definition.Name}", writable: true);
                key?.SetValue("Description", description);
            }

            return null;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static Win32Exception Last(string what)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{what}: {new Win32Exception(error).Message}");
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerCreateService = 0x0002;
    private const uint ServiceStop = 0x0020;
    private const uint Delete_ = 0x00010000;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceControlStop = 1;

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
    private static partial IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(IntPtr service);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref int info);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
