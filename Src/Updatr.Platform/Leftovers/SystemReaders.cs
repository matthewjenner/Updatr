using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Updatr.Platform.Leftovers;

/// <summary>Resolves .lnk shortcut targets through the shell's own ShellLink object.</summary>
public static class ShellLink
{
    /// <summary>The shortcut's target path, or null when it has none (URLs, shell items) or cannot be read.</summary>
    public static string? TargetOf(string lnkPath)
    {
        try
        {
            IShellLinkW link = (IShellLinkW)new ShellLinkObject();

            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                StringBuilder target = new(1024);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                return target.Length == 0 ? null : Environment.ExpandEnvironmentVariables(target.ToString());
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
    }
}

/// <summary>A Win32 service from its registry definition (readable unelevated).</summary>
public sealed record ServiceInfo(string Name, string? DisplayName, string? ImagePath);

public static class ServiceReader
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>User-mode services (own or shared process); drivers are never offered.</summary>
    public static IReadOnlyList<ServiceInfo> ReadAll()
    {
        List<ServiceInfo> services = [];
        using RegistryKey? root = Registry.LocalMachine.OpenSubKey(ServicesPath);

        foreach (string name in root?.GetSubKeyNames() ?? [])
        {
            using RegistryKey? key = root!.OpenSubKey(name);

            if (key?.GetValue("Type") is int type && (type & 0x30) != 0 && key.GetValue("ImagePath") is string image)
                services.Add(new ServiceInfo(name, key.GetValue("DisplayName") as string, LeftoverRules.ExecutableOf(image)));
        }

        return services;
    }
}

/// <summary>A scheduled task and the programs its actions run.</summary>
public sealed record TaskInfo(string Path, IReadOnlyList<string> Programs);

/// <summary>Scheduled tasks through the Task Scheduler COM API (late-bound; collections are 1-based and indexed).</summary>
public static class TaskReader
{
    /// <summary>Every task outside \Microsoft\ (Windows' own are never offered).</summary>
    public static IReadOnlyList<TaskInfo> ReadAll()
    {
        List<TaskInfo> tasks = [];

        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available."))!;
            service.Connect();
            Walk(service.GetFolder("\\"), tasks);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            // Unreadable here: nothing to offer.
        }

        return tasks;
    }

    private static void Walk(dynamic folder, List<TaskInfo> tasks)
    {
        if (((string)folder.Path).StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            dynamic list = folder.GetTasks(1); // TASK_ENUM_HIDDEN

            for (int i = 1; i <= (int)list.Count; i++)
            {
                dynamic task = list.Item(i);
                List<string> programs = [];

                try
                {
                    dynamic actions = task.Definition.Actions;

                    for (int a = 1; a <= (int)actions.Count; a++)
                    {
                        dynamic action = actions.Item(a);

                        if ((int)action.Type == 0 && action.Path is string path) // TASK_ACTION_EXEC
                            programs.Add(Environment.ExpandEnvironmentVariables(path.Trim('"')));
                    }
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    // Definition not readable unelevated: the task's name still counts.
                }

                tasks.Add(new TaskInfo((string)task.Path, programs));
            }

            dynamic folders = folder.GetFolders(0);

            for (int i = 1; i <= (int)folders.Count; i++)
                Walk(folders.Item(i), tasks);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // A folder only administrators can read.
        }
    }
}

/// <summary>A firewall rule's identity and the program it applies to.</summary>
public sealed record FirewallRuleInfo(string Name, string? Program);

/// <summary>Windows Firewall rules through its COM API (HNetCfg.FwPolicy2), read-only.</summary>
public static class FirewallReader
{
    /// <summary>Every rule that is not part of a built-in group (those carry "@" resource names).</summary>
    public static IReadOnlyList<FirewallRuleInfo> ReadAll()
    {
        List<FirewallRuleInfo> rules = [];

        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new COMException("Windows Firewall is not available."))!;

            foreach (dynamic rule in (IEnumerable)policy.Rules)
            {
                string? grouping = rule.Grouping as string;

                if (grouping?.StartsWith('@') == true || rule.Name is not string name)
                    continue;

                rules.Add(new FirewallRuleInfo(name, rule.ApplicationName is string program ? Environment.ExpandEnvironmentVariables(program) : null));
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            // Firewall service off or unreadable: nothing to offer.
        }

        return rules;
    }
}
