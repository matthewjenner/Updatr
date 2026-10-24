using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Updatr.Platform.Elevation;

/// <summary>Named-pipe hardening shared by the broker's server and client ends.</summary>
internal static partial class PipeSecurityHelpers
{
    /// <summary>
    /// Only the signed-in user may open the pipe, and never from another machine. The elevated
    /// broker runs as the same user (UAC split token), so the unelevated client matches.
    /// </summary>
    public static PipeSecurity CurrentUserOnly()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("No user SID for the current process.");

        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    public static int ClientProcessId(NamedPipeServerStream pipe)
        => GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? (int)pid : -1;

    public static int ServerProcessId(NamedPipeClientStream pipe)
        => GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid) ? (int)pid : -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
