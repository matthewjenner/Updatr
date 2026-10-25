using Microsoft.Win32;
using Updatr.Core.Abstractions;
using Updatr.Core.Carts;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;

namespace Updatr.Platform.Leftovers;

/// <summary>
/// Before/after snapshots around installs Updatr runs. The difference (new folders, keys,
/// shortcuts, services, tasks, firewall rules) is the install's footprint, saved per machine in
/// <c>footprints/&lt;machine&gt;/</c>; a later uninstall's scan lists what remains of it as high confidence.
/// </summary>
public sealed class Footprints(IDataStore store, string machineId) : IFootprintRecorder
{
    public Task<object?> BeforeAsync(CancellationToken cancellationToken)
        => Task.Run<object?>(Snapshot, cancellationToken);

    public async Task AfterAsync(object? before, PackageKey package, CancellationToken cancellationToken)
    {
        if (before is not HashSet<string> earlier)
            return;

        HashSet<string> later = await Task.Run(Snapshot, cancellationToken).ConfigureAwait(false);
        List<FootprintItem> added = [.. later.Except(earlier).Select(Parse).OfType<FootprintItem>()];

        if (added.Count > 0)
            await store.WriteTextAsync(PathFor(package), LeftoverJson.Serialize(added), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FootprintItem>> LoadAsync(PackageKey package, CancellationToken cancellationToken)
    {
        try
        {
            string? json = await store.ReadTextAsync(PathFor(package), cancellationToken).ConfigureAwait(false);
            return json is null ? [] : LeftoverJson.DeserializeFootprint(json) ?? [];
        }
        catch (Exception ex) when (ex is DataStoreException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private string PathFor(PackageKey package) => $"footprints/{machineId}/{CartStore.Slug(package.Source)}-{CartStore.Slug(package.Id)}.json";

    /// <summary>Everything a footprint can contain, as "Kind|Location" strings.</summary>
    private static HashSet<string> Snapshot()
    {
        HashSet<string> items = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string root, _) in LeftoverScanner.FolderRoots())
        {
            foreach (string child in Children(root))
            {
                items.Add($"{LeftoverKind.Folder}|{child}");

                foreach (string grandchild in Children(child))
                    items.Add($"{LeftoverKind.Folder}|{grandchild}");
            }
        }

        foreach ((RegistryHive hive, RegistryView view, string path, string display, _) in LeftoverScanner.RegistryRoots())
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(path);

            foreach (string name in root?.GetSubKeyNames() ?? [])
            {
                items.Add($@"{LeftoverKind.RegistryKey}|{display}\{name}");
                using RegistryKey? child = root!.OpenSubKey(name);

                foreach (string sub in child?.GetSubKeyNames() ?? [])
                    items.Add($@"{LeftoverKind.RegistryKey}|{display}\{name}\{sub}");
            }
        }

        foreach (string folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        })
        {
            foreach (string lnk in Files(folder, "*.lnk", recurse: true))
                items.Add($"{LeftoverKind.Shortcut}|{lnk}");
        }

        foreach (string folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        })
        {
            foreach (string lnk in Files(folder, "*.lnk", recurse: false))
                items.Add($"{LeftoverKind.Shortcut}|{lnk}");
        }

        foreach (ServiceInfo service in ServiceReader.ReadAll())
            items.Add($"{LeftoverKind.Service}|{service.Name}");

        foreach (TaskInfo task in TaskReader.ReadAll())
            items.Add($"{LeftoverKind.ScheduledTask}|{task.Path}");

        foreach (FirewallRuleInfo rule in FirewallReader.ReadAll())
            items.Add($"{LeftoverKind.FirewallRule}|{rule.Name}");

        return items;
    }

    private static FootprintItem? Parse(string item)
    {
        int bar = item.IndexOf('|');
        return bar > 0 && Enum.TryParse(item[..bar], out LeftoverKind kind) ? new FootprintItem(kind, item[(bar + 1)..]) : null;
    }

    private static IEnumerable<string> Children(string folder)
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

    private static IEnumerable<string> Files(string folder, string pattern, bool recurse)
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
}

/// <summary>A stable, file-safe name for this PC: computer name plus the start of its machine GUID.</summary>
public static class MachineIdentity
{
    public static string Id { get; } = Compute();

    private static string Compute()
    {
        string guid = string.Empty;

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = (key?.GetValue("MachineGuid") as string ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Fall back to the computer name alone.
        }

        return CartStore.Slug($"{Environment.MachineName}-{(guid.Length >= 8 ? guid[..8] : "pc")}");
    }
}
