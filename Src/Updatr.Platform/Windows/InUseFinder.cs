using System.Diagnostics;
using System.Runtime.InteropServices;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;
using Updatr.Platform.Leftovers;

namespace Updatr.Platform.Windows;

/// <summary>
/// Asks Windows' Restart Manager which programs have an app's files open - the same question an
/// installer asks before it says "in use". Looks in the app's install folder, its uninstaller's
/// folder, and the few places an app keeps loaded files outside them (<see cref="ExtraFolders"/>).
/// Read-only: it names programs, it never closes them.
/// </summary>
public sealed partial class InUseFinder(ILeftoverScanner scanner) : IInUseFinder
{
    /// <summary>Registering stops after this many files; the program files that matter are near the top.</summary>
    private const int FileLimit = 5_000;

    /// <summary>
    /// Files an installer checks outside the app's own folders. OBS's installer refuses to run while
    /// its game-capture hook is loaded anywhere - and the hook loads into any Vulkan program
    /// (Ollama, games), not just OBS (Phase 8 polish finding).
    /// </summary>
    private static readonly Dictionary<string, Func<string[]>> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OBSProject.OBSStudio"] = () => [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio-hook")],
    };

    public async Task<IReadOnlyList<BlockingProcess>> FindAsync(InstalledPackage package, CancellationToken cancellationToken)
    {
        LeftoverTarget? target = await scanner.DescribeAsync(package, cancellationToken).ConfigureAwait(false);
        List<string> folders = [.. new[] { target?.InstallLocation, target?.UninstallerFolder }.OfType<string>()];

        if (ExtraFolders.TryGetValue(package.Id, out Func<string[]>? extra))
            folders.AddRange(extra());

        return await Task.Run(() => Find(folders), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The programs using any .exe or .dll under the folders, this process excluded.</summary>
    public static IReadOnlyList<BlockingProcess> Find(IEnumerable<string> folders)
    {
        string[] roots = [.. BroadRoots()];
        string[] files = [.. folders.Select(LeftoverRules.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => f.Length > 3 && Directory.Exists(f) && !roots.Contains(f, StringComparer.OrdinalIgnoreCase))
            .SelectMany(ProgramFiles).Take(FileLimit)];

        if (files.Length == 0)
            return [];

        List<BlockingProcess> found = [];

        foreach ((int pid, string appName) in RestartManager.Users(files))
        {
            if (pid == Environment.ProcessId || found.Any(f => f.ProcessId == pid))
                continue;

            string name = ProcessName(pid) ?? appName;
            string? note = name.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase) ? HiddenTerminalNote(pid) : null;
            found.Add(new BlockingProcess(name, pid, note));
        }

        // Terminal's console helper goes when Terminal does; naming it too only adds noise.
        return found.Any(f => f.Name.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase))
            ? [.. found.Where(f => !f.Name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase))]
            : found;
    }

    /// <summary>Drive roots and Program Files-style roots: a broken registration pointing at one would register half the disk.</summary>
    private static IEnumerable<string> BroadRoots()
        => LeftoverScanner.FolderRoots().Select(r => LeftoverRules.Normalize(r.Root));

    private static IEnumerable<string> ProgramFiles(string folder)
    {
        EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

        try
        {
            return Directory.EnumerateFiles(folder, "*", options)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // Gone already.
        }
    }

    /// <summary>
    /// Windows Terminal with no window. When Terminal is the default console, a console program
    /// started in the background (SimBridge with --hide) gets a hidden Terminal of its own, which
    /// keeps Terminal's update from applying until that program exits. The program's console host
    /// starts a moment before the Terminal does - matching on that names it.
    /// </summary>
    private static string? HiddenTerminalNote(int pid)
    {
        DateTime started;

        try
        {
            using Process terminal = Process.GetProcessById(pid);

            if (terminal.MainWindowHandle != IntPtr.Zero)
                return null;

            started = terminal.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        Dictionary<int, int> parents = ProcessTree.Parents();
        (TimeSpan Gap, int Pid)? closest = null;

        foreach (Process host in Process.GetProcessesByName("conhost"))
        {
            using (host)
            {
                try
                {
                    // Measured: the client's host about 0.1 s before Terminal; another console opened
                    // in the same second (a shell) 0.5 s before - so the closest one, not all.
                    TimeSpan gap = started - host.StartTime;

                    if (gap >= TimeSpan.FromSeconds(-0.5) && gap <= TimeSpan.FromSeconds(5) && parents.TryGetValue(host.Id, out int client)
                        && (closest is null || gap.Duration() < closest.Value.Gap))
                        closest = (gap.Duration(), client);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Elevated or gone; skip it.
                }
            }
        }

        return closest is { } c && ProcessName(c.Pid) is { } name
            ? $"no window - it is hosting {name} (pid {c.Pid}) in the background; close that program"
            : "no window - it is hosting a program running in the background";
    }

    /// <summary>Restart Manager (rstrtmgr.dll): which processes have these files open.</summary>
    private static partial class RestartManager
    {
        private const int SessionKeyLength = 33; // CCH_RM_SESSION_KEY + 1
        private const int ErrorMoreData = 234;

        public static IReadOnlyList<(int Pid, string AppName)> Users(string[] files)
        {
            uint session;

            unsafe
            {
                char* key = stackalloc char[SessionKeyLength];

                if (RmStartSession(out session, 0, key) != 0)
                    return [];
            }

            try
            {
                if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0)
                    return [];

                uint count = 0;
                uint reasons = 0;
                int result = RmGetList(session, out uint needed, ref count, null, ref reasons);

                if (result != ErrorMoreData || needed == 0)
                    return [];

                RmProcessInfo[] info = new RmProcessInfo[needed + 4]; // Room for a process that starts between the calls.
                count = (uint)info.Length;

                if (RmGetList(session, out _, ref count, info, ref reasons) != 0)
                    return [];

                return [.. info.Take((int)count).Select(i => (i.Process.ProcessId, i.AppName))];
            }
            finally
            {
                _ = RmEndSession(session);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;

            // A FILETIME: two DWORDs, so 4-byte aligned - a long here would pad the struct and shift every entry after the first.
            public uint StartTimeLow;
            public uint StartTimeHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            public fixed char AppNameChars[256];
            public fixed char ServiceShortName[64];
            public int ApplicationType;
            public uint AppStatus;
            public uint SessionId;
            public int Restartable;

            public string AppName
            {
                get
                {
                    fixed (char* name = AppNameChars)
                        return new string(name);
                }
            }
        }

        [LibraryImport("rstrtmgr.dll")]
        private static unsafe partial int RmStartSession(out uint session, int flags, char* sessionKey);

        [LibraryImport("rstrtmgr.dll")]
        private static partial int RmEndSession(uint session);

        [LibraryImport("rstrtmgr.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);

        [LibraryImport("rstrtmgr.dll")]
        private static partial int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint rebootReasons);
    }

    /// <summary>Process ids to parent ids, from a Toolhelp snapshot.</summary>
    private static partial class ProcessTree
    {
        public static unsafe Dictionary<int, int> Parents()
        {
            Dictionary<int, int> parents = [];
            IntPtr snapshot = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS

            if (snapshot == new IntPtr(-1))
                return parents;

            try
            {
                ProcessEntry entry = new() { Size = (uint)sizeof(ProcessEntry) };

                for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            finally
            {
                _ = CloseHandle(snapshot);
            }

            return parents;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            public fixed char ExeFile[260];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
