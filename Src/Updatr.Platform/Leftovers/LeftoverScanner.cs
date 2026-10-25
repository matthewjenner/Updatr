using Microsoft.Win32;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;
using Updatr.Platform.Data;
using Updatr.Platform.WinGet;

namespace Updatr.Platform.Leftovers;

/// <summary>
/// Finds what an uninstaller left behind: the app's folders and keys, services and tasks that run
/// its programs, shortcuts and firewall rules that point into it. Read-only; the user picks what
/// to quarantine.
/// </summary>
public sealed class LeftoverScanner(string dataRoot) : ILeftoverScanner
{
    /// <summary>Sizing stops after this many files or this long ("at least" this big), so the scan stays quick.</summary>
    private const int SizeFileLimit = 50_000;

    private static readonly TimeSpan SizeTimeLimit = TimeSpan.FromSeconds(2);

    public Task<LeftoverTarget?> DescribeAsync(InstalledPackage package, CancellationToken cancellationToken)
        => Task.Run(() => Describe(package), cancellationToken);

    /// <summary>How long to wait for an uninstaller that finishes in the background to remove its own registration.</summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<Leftover>> ScanAsync(LeftoverTarget target, CancellationToken cancellationToken)
    {
        // Many uninstallers (NSIS, Inno, Squirrel) copy themselves to temp, return at once, and
        // finish in the background. Scanning now would list what they are about to remove.
        if (target.UninstallKey is { } key)
        {
            DateTime deadline = DateTime.UtcNow + SettleTimeout;

            while (RegistryKeyExists(key) && DateTime.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Scan(target, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Leftover>> StillPresentAsync(IReadOnlyList<Leftover> items, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<Leftover>>(() =>
        {
            Lazy<HashSet<string>> tasks = new(() => [.. TaskReader.ReadAll().Select(t => t.Path)]);
            Lazy<HashSet<string>> rules = new(() => [.. FirewallReader.ReadAll().Select(r => r.Name)]);

            return [.. items.Where(i => i.Kind switch
            {
                LeftoverKind.Folder => Directory.Exists(i.Location),
                LeftoverKind.Shortcut => File.Exists(i.Location),
                LeftoverKind.RegistryKey => RegistryKeyExists(i.Location),
                LeftoverKind.Service => ServiceControl.Read(i.Location) is not null,
                LeftoverKind.ScheduledTask => tasks.Value.Contains(i.Location),
                LeftoverKind.FirewallRule => rules.Value.Contains(i.Location),
                _ => true,
            })];
        }, cancellationToken);

    /// <summary>Folder roots apps keep files in, with whether changing them needs administrator access.</summary>
    public static IReadOnlyList<(string Root, bool Machine)> FolderRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return [.. new (string, bool)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), false),
            (local, false),
            (Path.Combine(local, "Programs"), false),
        }.Where(r => r.Item1.Length > 0).Distinct()];
    }

    /// <summary>Registry roots apps keep settings in: (hive, view, path, display prefix, machine).</summary>
    public static IReadOnlyList<(RegistryHive Hive, RegistryView View, string Path, string Display, bool Machine)> RegistryRoots() =>
    [
        (RegistryHive.CurrentUser, RegistryView.Registry64, "Software", @"HKCU\Software", false),
        (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE", @"HKLM\SOFTWARE", true),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node", @"HKLM\SOFTWARE\WOW6432Node", true),
    ];

    private static LeftoverTarget? Describe(InstalledPackage package)
    {
        if (package.Source != WinGetComSource.SourceId)
            return null; // npm, pipx, dotnet tools, VS Code, PSGallery remove themselves cleanly.

        InstalledDetails? details = null;

        try
        {
            details = WinGetComSource.Describe(package.Id);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException or PackageSourceException)
        {
            // Fall back to the Uninstall registration below.
        }

        IReadOnlyList<UninstallEntry> entries = MachineInventory.Read();
        HashSet<string> codes = new(details?.ProductCodes ?? [], StringComparer.OrdinalIgnoreCase);

        // ARP ids from winget's local catalog end in the Uninstall subkey name.
        if (package.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            codes.Add(package.Id[(package.Id.LastIndexOf('\\') + 1)..]);

        UninstallEntry? entry = entries.FirstOrDefault(e => codes.Contains(e.KeyName))
            ?? entries.FirstOrDefault(e => string.Equals(e.DisplayName, details?.Name ?? package.Name, StringComparison.OrdinalIgnoreCase));

        string? uninstaller = LeftoverRules.ExecutableOf(details?.UninstallCommand ?? entry?.UninstallString);
        string? uninstallerFolder = uninstaller is not null && !uninstaller.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(uninstaller)
            : null;

        return new LeftoverTarget
        {
            Package = package.Key,
            Name = details?.Name ?? entry?.DisplayName ?? package.Name,
            Publisher = details?.Publisher ?? entry?.Publisher ?? package.Publisher,
            InstallLocation = NonEmpty(details?.InstallLocation) ?? NonEmpty(entry?.InstallLocation),
            UninstallerFolder = uninstallerFolder,
            UninstallKey = entry?.Key,
        };
    }

    private IReadOnlyList<Leftover> Scan(LeftoverTarget target, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> appKeys = LeftoverRules.AppKeys(target);
        string publisherKey = LeftoverRules.PublisherKey(target.Publisher);
        IReadOnlyList<UninstallEntry> others = MachineInventory.Read();

        // Anything another installed app owns - and Updatr itself, and Windows - is never offered.
        IReadOnlyList<(string Root, bool Machine)> roots = FolderRoots();
        string[] rootPaths = [.. roots.Select(r => r.Root)];

        // A registration pointing at a root or a drive ("C:\Program Files", "C:\") would protect
        // everything under it; such entries are broken, not meaningful, so they are ignored.
        bool Meaningful(string path) => LeftoverRules.Normalize(path) is { Length: > 3 } p && !rootPaths.Any(r => LeftoverRules.IsUnder(r, p));

        List<string> protectedPaths =
        [
            .. others.Where(e => !string.Equals(e.Key, target.UninstallKey, StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.OwnedFolders()).Where(Meaningful),
            AppLocations.ExeDirectory,
            dataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ];

        bool publisherShared = publisherKey.Length > 0 && others.Any(e =>
            !string.Equals(e.Key, target.UninstallKey, StringComparison.OrdinalIgnoreCase) && LeftoverRules.PublisherKey(e.Publisher) == publisherKey);

        string[] appFolders = [.. new[] { target.InstallLocation, target.UninstallerFolder }.OfType<string>().Select(LeftoverRules.Normalize)
            .Where(f => !protectedPaths.Any(p => LeftoverRules.IsUnder(f, p) || LeftoverRules.IsUnder(p, f)))];

        Dictionary<string, (LeftoverKind Kind, string Location, Confidence Confidence, string Reason, long? Size, bool Admin)> found = new(StringComparer.OrdinalIgnoreCase);

        void Add(LeftoverKind kind, string location, Confidence confidence, string reason, bool admin, long? size = null)
        {
            string key = $"{kind}|{location}";

            if (!found.TryGetValue(key, out var existing) || existing.Confidence < confidence)
                found[key] = (kind, location, confidence, reason, size, admin);
        }

        bool PointsIntoApp(string? path) => path is not null && appFolders.Any(f => LeftoverRules.IsUnder(path, f));

        bool MachineFolder(string path) => roots.Any(r => r.Machine && LeftoverRules.IsUnder(path, r.Root));

        // 1. The app's own folders, still there.
        foreach (string folder in appFolders.Where(Directory.Exists))
        {
            if (LeftoverRules.IsOfferableFolder(folder, rootPaths, protectedPaths))
                Add(LeftoverKind.Folder, folder, Confidence.High, "The app's install folder is still there", MachineFolder(folder));
        }

        // 2. Folders named after the app (or publisher\app) where apps keep files.
        foreach ((string root, bool machine) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string child in SafeDirectories(root))
            {
                string name = Path.GetFileName(child);

                if (LeftoverRules.MatchName(name, appKeys) is { } confidence && LeftoverRules.IsOfferableFolder(child, rootPaths, protectedPaths))
                    Add(LeftoverKind.Folder, child, confidence, confidence == Confidence.Medium ? "Named after the app" : "Name resembles the app", machine);

                if (publisherKey.Length == 0 || LeftoverRules.Key(name) != publisherKey)
                    continue;

                foreach (string grandchild in SafeDirectories(child))
                {
                    if (LeftoverRules.MatchName(Path.GetFileName(grandchild), appKeys) is { } inner && LeftoverRules.IsOfferableFolder(grandchild, rootPaths, protectedPaths))
                        Add(LeftoverKind.Folder, grandchild, inner, $"In the publisher's folder ({name}) and named after the app", machine);
                }

                // The whole publisher folder, only when nothing else from that publisher is installed.
                if (!publisherShared && LeftoverRules.Usable(publisherKey) && LeftoverRules.IsOfferableFolder(child, rootPaths, protectedPaths))
                    Add(LeftoverKind.Folder, child, Confidence.Low, $"The publisher's folder; no other {target.Publisher} app is installed", machine);
            }
        }

        // 3. Registry keys: the app's Uninstall entry, and keys named after it.
        if (target.UninstallKey is { } uninstallKey && RegistryKeyExists(uninstallKey))
            Add(LeftoverKind.RegistryKey, uninstallKey, Confidence.High, "The app's uninstall entry is still registered", uninstallKey.StartsWith("HKLM", StringComparison.Ordinal));

        foreach ((RegistryHive hive, RegistryView view, string path, string display, bool machine) in RegistryRoots())
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(path);

            foreach (string name in root?.GetSubKeyNames() ?? [])
            {
                if (LeftoverRules.MatchName(name, appKeys) is { } confidence)
                    Add(LeftoverKind.RegistryKey, $@"{display}\{name}", confidence, confidence == Confidence.Medium ? "Settings key named after the app" : "Key name resembles the app", machine);

                if (publisherKey.Length == 0 || LeftoverRules.Key(name) != publisherKey)
                    continue;

                using RegistryKey? publisher = root!.OpenSubKey(name);

                foreach (string child in publisher?.GetSubKeyNames() ?? [])
                {
                    if (LeftoverRules.MatchName(child, appKeys) is { } inner)
                        Add(LeftoverKind.RegistryKey, $@"{display}\{name}\{child}", inner, $"Under the publisher's key ({name}) and named after the app", machine);
                }

                if (!publisherShared && LeftoverRules.Usable(publisherKey))
                    Add(LeftoverKind.RegistryKey, $@"{display}\{name}", Confidence.Low, $"The publisher's key; no other {target.Publisher} app is installed", machine);
            }
        }

        // 4. Services and scheduled tasks that run the app's programs.
        foreach (ServiceInfo service in ServiceReader.ReadAll())
        {
            if (PointsIntoApp(service.ImagePath))
                Add(LeftoverKind.Service, service.Name, Confidence.High, $"Runs {service.ImagePath}", true);
            else if (appKeys.Contains(LeftoverRules.Key(service.Name)) || (service.DisplayName is { } d && !d.StartsWith('@') && appKeys.Contains(LeftoverRules.Key(d))))
                Add(LeftoverKind.Service, service.Name, Confidence.Medium, "Service named after the app", true);
        }

        foreach (TaskInfo task in TaskReader.ReadAll())
        {
            string taskName = task.Path[(task.Path.LastIndexOf('\\') + 1)..];

            if (task.Programs.Any(PointsIntoApp))
                Add(LeftoverKind.ScheduledTask, task.Path, Confidence.High, $"Runs {task.Programs.First(PointsIntoApp)}", true);
            else if (LeftoverRules.MatchName(taskName, appKeys) is not null || appKeys.Any(a => a.Length >= 5 && LeftoverRules.Key(taskName).Contains(a, StringComparison.Ordinal)))
                Add(LeftoverKind.ScheduledTask, task.Path, Confidence.Low, "Task name mentions the app", true);
        }

        // 5. Shortcuts into the app (or broken ones named after it) and empty Start menu folders.
        foreach ((string folder, bool machine, bool recurse) in ShortcutFolders())
        {
            foreach (string lnk in SafeFiles(folder, "*.lnk", recurse))
            {
                string? targetPath = ShellLink.TargetOf(lnk);

                if (PointsIntoApp(targetPath))
                    Add(LeftoverKind.Shortcut, lnk, Confidence.High, $"Points into the app's folder ({targetPath})", machine);
                else if (targetPath is not null && !File.Exists(targetPath) && LeftoverRules.MatchName(Path.GetFileNameWithoutExtension(lnk), appKeys) is not null)
                    Add(LeftoverKind.Shortcut, lnk, Confidence.Medium, "Named after the app, and its target is gone", machine);
            }
        }

        // 6. Firewall rules for the app's programs.
        foreach (FirewallRuleInfo rule in FirewallReader.ReadAll())
        {
            if (PointsIntoApp(rule.Program))
                Add(LeftoverKind.FirewallRule, rule.Name, Confidence.High, $"Allows {rule.Program}", true);
            else if (rule.Program is not null && !File.Exists(rule.Program) && appKeys.Contains(LeftoverRules.Key(rule.Name)))
                Add(LeftoverKind.FirewallRule, rule.Name, Confidence.Medium, "Named after the app, and its program is gone", true);
        }

        // 7. What Updatr recorded when it installed the app.
        foreach (FootprintItem item in target.Footprint)
        {
            bool exists = item.Kind switch
            {
                LeftoverKind.Folder => Directory.Exists(item.Location) && LeftoverRules.IsOfferableFolder(item.Location, rootPaths, protectedPaths),
                LeftoverKind.Shortcut => File.Exists(item.Location),
                LeftoverKind.RegistryKey => RegistryKeyExists(item.Location),
                _ => found.ContainsKey($"{item.Kind}|{item.Location}"),
            };

            if (exists)
                Add(item.Kind, item.Location, Confidence.High, "Created when Updatr installed the app", item.Kind != LeftoverKind.Shortcut || MachineFolder(item.Location) || item.Location.StartsWith("HKLM", StringComparison.Ordinal));
        }

        // A folder inside another listed folder goes with it; list only the outer one.
        string[] folders = [.. found.Values.Where(v => v.Kind == LeftoverKind.Folder).Select(v => v.Location)];
        int id = 0;

        return [.. found.Values
            .Where(v => v.Kind != LeftoverKind.Folder || !folders.Any(f => !f.Equals(v.Location, StringComparison.OrdinalIgnoreCase) && LeftoverRules.IsUnder(v.Location, f)))
            .Where(v => v.Kind != LeftoverKind.Shortcut || !folders.Any(f => LeftoverRules.IsUnder(v.Location, f)))
            .OrderByDescending(v => v.Confidence).ThenBy(v => v.Kind).ThenBy(v => v.Location, StringComparer.OrdinalIgnoreCase)
            .Select(v => new Leftover((++id).ToString(System.Globalization.CultureInfo.InvariantCulture), v.Kind, v.Location, v.Confidence, v.Reason,
                v.Kind == LeftoverKind.Folder ? FolderSize(v.Location) : v.Size, v.Admin))];
    }

    /// <summary>Start menus are searched through; desktops only at the top (people keep whole archives on them).</summary>
    private static IEnumerable<(string Folder, bool Machine, bool Recurse)> ShortcutFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Programs), false, true);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true, true);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false, false);
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), true, false);
    }

    /// <summary>"HKLM\SOFTWARE\X" / "HKCU\Software\X" (WOW6432Node spelled out) exists.</summary>
    public static bool RegistryKeyExists(string location)
    {
        if (ParseKey(location) is not { } key)
            return false;

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(key.Hive, RegistryView.Registry64);
        using RegistryKey? found = baseKey.OpenSubKey(key.Path);
        return found is not null;
    }

    /// <summary>Splits a display key path into hive and subkey path.</summary>
    public static (RegistryHive Hive, string Path)? ParseKey(string location)
    {
        int slash = location.IndexOf('\\');

        if (slash <= 0)
            return null;

        RegistryHive? hive = location[..slash].ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            _ => null,
        };

        return hive is null ? null : (hive.Value, location[(slash + 1)..]);
    }

    private static long? FolderSize(string folder)
    {
        long total = 0;
        int files = 0;
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                total += file.Length;

                if (++files >= SizeFileLimit || timer.Elapsed > SizeTimeLimit)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return total == 0 ? null : total;
        }

        return total;
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string folder, string pattern, bool recurse)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true }).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
