using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Updatr.Core.Leftovers;
using Updatr.Platform.Debloat;

namespace Updatr.Platform.Leftovers;

/// <summary>
/// The file-level work of quarantine, run in this process or (for machine-wide items) the elevated
/// broker. An entry is a folder: <c>manifest.json</c> plus one stored file per item
/// (<c>item-N.zip</c>, <c>item-N.registry.json</c> + <c>.reg</c>, <c>item-N.service.json</c>,
/// <c>item-N.task.xml</c>, <c>item-N.firewall.json</c>). Each item is stored, and the store
/// checked, before the original is removed.
/// </summary>
public static class QuarantineEngine
{
    public const string ManifestFile = "manifest.json";

    public static QuarantineEntry Move(string entryDir, string app, IReadOnlyList<Leftover> items)
    {
        Directory.CreateDirectory(entryDir);
        List<QuarantinedItem> results = [];

        for (int i = 0; i < items.Count; i++)
        {
            Leftover item = items[i];
            string stem = Path.Combine(entryDir, $"item-{i + 1}");

            try
            {
                if ((item.Kind == LeftoverKind.Folder && LeftoverRules.IsUnder(entryDir, item.Location) ? "it holds Updatr's own quarantine" : LeftoverSafety.Problem(item.Kind, item.Location)) is { } unsafeReason)
                {
                    results.Add(new QuarantinedItem(item.Kind, item.Location, false, unsafeReason));
                    continue;
                }

                string? leftBehind = item.Kind switch
                {
                    LeftoverKind.Folder => MoveFolder(item.Location, stem + ".zip", item.SizeBytes),
                    LeftoverKind.Shortcut => MoveFile(item.Location, stem + ".zip"),
                    LeftoverKind.RegistryKey => MoveRegistry(item.Location, stem),
                    LeftoverKind.Service => MoveService(item.Location, stem + ".service.json"),
                    LeftoverKind.ScheduledTask => MoveTask(item.Location, stem + ".task.xml"),
                    LeftoverKind.FirewallRule => MoveFirewall(item.Location, stem + ".firewall.json"),
                    _ => throw new NotSupportedException(item.Kind.ToString()),
                };

                results.Add(new QuarantinedItem(item.Kind, item.Location, true, leftBehind));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception
                or System.Security.SecurityException or InvalidOperationException or ArgumentException)
            {
                // Nothing was removed for this item (removal only follows a complete store).
                foreach (string partial in Directory.EnumerateFiles(entryDir, $"item-{i + 1}.*"))
                    TryDelete(partial);

                results.Add(new QuarantinedItem(item.Kind, item.Location, false, ex.Message.Trim()));
            }
        }

        QuarantineEntry entry = new(Path.GetFileName(entryDir), DateTimeOffset.Now, app, results, DirectorySize(entryDir));
        File.WriteAllText(Path.Combine(entryDir, ManifestFile), LeftoverJson.Serialize(entry));
        return entry;
    }

    /// <summary>Puts every stored item back. Returns the problems (empty when all went back).</summary>
    public static IReadOnlyList<string> Restore(string entryDir)
    {
        QuarantineEntry entry = Read(entryDir) ?? throw new InvalidDataException($"{entryDir} has no readable manifest.");
        List<string> problems = [];

        for (int i = 0; i < entry.Items.Count; i++)
        {
            QuarantinedItem item = entry.Items[i];

            if (!item.Stored)
                continue;

            string stem = Path.Combine(entryDir, $"item-{i + 1}");

            try
            {
                string? problem = item.Kind switch
                {
                    LeftoverKind.Folder => RestoreZip(stem + ".zip", item.Location),
                    LeftoverKind.Shortcut => RestoreZip(stem + ".zip", Path.GetDirectoryName(item.Location)!),
                    LeftoverKind.RegistryKey => RestoreRegistry(item.Location, stem + ".registry.json"),
                    LeftoverKind.Service => ServiceControl.Create(Json.Read(stem + ".service.json", QuarantineJsonContext.Default.ServiceDefinition)),
                    LeftoverKind.ScheduledTask => RestoreTask(item.Location, stem + ".task.xml"),
                    LeftoverKind.FirewallRule => RestoreFirewall(stem + ".firewall.json"),
                    _ => $"cannot restore {item.Kind}",
                };

                if (problem is not null)
                    problems.Add($"{item.Location}: {problem}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception
                or System.Security.SecurityException or InvalidDataException or JsonException)
            {
                problems.Add($"{item.Location}: {ex.Message.Trim()}");
            }
        }

        return problems;
    }

    public static QuarantineEntry? Read(string entryDir)
    {
        string manifest = Path.Combine(entryDir, ManifestFile);

        try
        {
            return File.Exists(manifest) ? LeftoverJson.DeserializeEntry(File.ReadAllText(manifest)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Folders and shortcuts ----------------------------------------------------------

    private static string? MoveFolder(string folder, string zipPath, long? sizeBytes)
    {
        if (!Directory.Exists(folder))
            throw new IOException("It is no longer there.");

        // The zip goes to the drive: refuse up front rather than fill it half way.
        string? drive = Path.GetPathRoot(Path.GetFullPath(zipPath));

        if (drive is not null && sizeBytes is { } size && new DriveInfo(drive).AvailableFreeSpace < size + (64L << 20))
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"Not enough space on the drive to keep a copy ({size / 1048576.0:0} MB)."));

        // Throws (and so leaves the folder alone) if any file cannot be read, e.g. while in use.
        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        try
        {
            Directory.Delete(folder, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"kept a copy, but some files could not be removed ({ex.Message.Trim()})";
        }
    }

    private static string? MoveFile(string file, string zipPath)
    {
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(file, Path.GetFileName(file));

        File.Delete(file);
        return null;
    }

    private static string? RestoreZip(string zipPath, string target)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        int skipped = 0;
        string root = Path.GetFullPath(target);

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // Zip-slip: an entry must land inside the target.
            if (!LeftoverRules.IsUnder(destination, root))
                continue;

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
        }

        return skipped == 0 ? null : $"{skipped} file(s) already existed and were left as they are";
    }

    // ---- Registry -----------------------------------------------------------------------

    private static string? MoveRegistry(string location, string stem)
    {
        (RegistryHive hive, string path) = LeftoverScanner.ParseKey(location) ?? throw new ArgumentException($"Not a registry key: {location}");
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        RegNode node;

        using (RegistryKey key = baseKey.OpenSubKey(path) ?? throw new IOException("It is no longer there."))
            node = Export(key, path[(path.LastIndexOf('\\') + 1)..]);

        File.WriteAllText(stem + ".registry.json", JsonSerializer.Serialize(node, QuarantineJsonContext.Default.RegNode));
        File.WriteAllText(stem + ".reg", ToReg(location, node), Encoding.Unicode);

        // Read back before deleting: the copy must be complete.
        if (Json.Read(stem + ".registry.json", QuarantineJsonContext.Default.RegNode).Name != node.Name)
            throw new IOException("The saved copy could not be read back.");

        baseKey.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return null;
    }

    private static string? RestoreRegistry(string location, string jsonPath)
    {
        (RegistryHive hive, string path) = LeftoverScanner.ParseKey(location) ?? throw new ArgumentException($"Not a registry key: {location}");
        RegNode node = Json.Read(jsonPath, QuarantineJsonContext.Default.RegNode);
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(path, writable: true);
        Import(key, node);
        return null;
    }

    private static RegNode Export(RegistryKey key, string name)
    {
        List<RegValue> values = [];

        foreach (string valueName in key.GetValueNames())
        {
            RegistryValueKind kind = key.GetValueKind(valueName);

            if (key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } value && kind != RegistryValueKind.Unknown && kind != RegistryValueKind.None)
                values.Add(new RegValue(valueName, kind.ToString(), RegistryTarget.Format(value, kind)));
        }

        List<RegNode> children = [];

        foreach (string child in key.GetSubKeyNames())
        {
            using RegistryKey? sub = key.OpenSubKey(child);

            if (sub is not null)
                children.Add(Export(sub, child));
        }

        return new RegNode(name, values, children);
    }

    /// <summary>Recreates keys and values; values that exist again are not overwritten.</summary>
    private static void Import(RegistryKey key, RegNode node)
    {
        foreach (RegValue value in node.Values)
        {
            if (key.GetValue(value.Name) is null)
            {
                RegistryValueKind kind = Enum.Parse<RegistryValueKind>(value.Kind);
                key.SetValue(value.Name, RegistryTarget.Parse(value.Data, kind), kind);
            }
        }

        foreach (RegNode child in node.Children)
        {
            using RegistryKey sub = key.CreateSubKey(child.Name, writable: true);
            Import(sub, child);
        }
    }

    /// <summary>A human-readable .reg of the key (strings and DWORDs exactly; other kinds as hex).</summary>
    private static string ToReg(string location, RegNode node)
    {
        StringBuilder text = new("Windows Registry Editor Version 5.00\r\n");
        string root = location.StartsWith("HKLM", StringComparison.Ordinal) ? "HKEY_LOCAL_MACHINE" + location[4..] : "HKEY_CURRENT_USER" + location[4..];
        Append(text, root, node, isRoot: true);
        return text.ToString();

        static void Append(StringBuilder text, string path, RegNode node, bool isRoot)
        {
            string here = isRoot ? path : $@"{path}\{node.Name}";
            text.Append(CultureInfo.InvariantCulture, $"\r\n[{here}]\r\n");

            foreach (RegValue value in node.Values)
            {
                string name = value.Name.Length == 0 ? "@" : Quote(value.Name);
                text.Append(value.Kind switch
                {
                    "String" => $"{name}={Quote(value.Data)}",
                    "DWord" => string.Create(CultureInfo.InvariantCulture, $"{name}=dword:{unchecked((uint)int.Parse(value.Data, CultureInfo.InvariantCulture)):x8}"),
                    _ => $"; {name} ({value.Kind}) is kept in the .json copy beside this file",
                }).Append("\r\n");
            }

            foreach (RegNode child in node.Children)
                Append(text, here, child, isRoot: false);
        }

        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    // ---- Services, tasks, firewall rules ------------------------------------------------

    private static string? MoveService(string name, string jsonPath)
    {
        ServiceDefinition definition = ServiceControl.Read(name) ?? throw new IOException("It is no longer there.");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(definition, QuarantineJsonContext.Default.ServiceDefinition));
        _ = Json.Read(jsonPath, QuarantineJsonContext.Default.ServiceDefinition);
        ServiceControl.Delete(name);
        return null;
    }

    private static string? MoveTask(string path, string xmlPath)
    {
        (dynamic folder, string name) = TaskFolder(path, create: false);
        dynamic task = folder.GetTask(name);
        File.WriteAllText(xmlPath, (string)task.Xml, Encoding.Unicode);
        folder.DeleteTask(name, 0);
        return null;
    }

    private static string? RestoreTask(string path, string xmlPath)
    {
        (dynamic folder, string name) = TaskFolder(path, create: true);
        string xml = File.ReadAllText(xmlPath);

        try
        {
            folder.RegisterTask(name, xml, 6 /* TASK_CREATE_OR_UPDATE */, null, null, 0 /* TASK_LOGON_NONE: the XML's principal */, null);
        }
        catch (COMException)
        {
            folder.RegisterTask(name, xml, 6, null, null, 3 /* TASK_LOGON_INTERACTIVE_TOKEN */, null);
        }

        return null;
    }

    private static (dynamic Folder, string Name) TaskFolder(string path, bool create)
    {
        int split = path.LastIndexOf('\\');
        string folderPath = split <= 0 ? "\\" : path[..split];
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available."))!;
        service.Connect();

        try
        {
            return (service.GetFolder(folderPath), path[(split + 1)..]);
        }
        catch (Exception ex) when (create && ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        {
            return (service.GetFolder("\\").CreateFolder(folderPath, null), path[(split + 1)..]);
        }
    }

    private static string? MoveFirewall(string name, string jsonPath)
    {
        dynamic policy = Firewall();
        List<FirewallRuleData> rules = [];

        foreach (dynamic rule in (IEnumerable)policy.Rules)
        {
            if (rule.Name is string ruleName && ruleName == name)
                rules.Add(FirewallRuleData.From(rule));
        }

        if (rules.Count == 0)
            throw new IOException("It is no longer there.");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(rules, QuarantineJsonContext.Default.ListFirewallRuleData));
        _ = Json.Read(jsonPath, QuarantineJsonContext.Default.ListFirewallRuleData);

        // Remove takes one rule per call when several share the name.
        for (int i = 0; i < rules.Count; i++)
            policy.Rules.Remove(name);

        return null;
    }

    private static string? RestoreFirewall(string jsonPath)
    {
        dynamic policy = Firewall();

        foreach (FirewallRuleData data in Json.Read(jsonPath, QuarantineJsonContext.Default.ListFirewallRuleData))
            policy.Rules.Add(data.ToRule());

        return null;
    }

    internal static dynamic Firewall()
        => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new COMException("Windows Firewall is not available."))!;

    // ---- Helpers ------------------------------------------------------------------------

    public static long DirectorySize(string folder)
        => Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial store file: harmless, and purged with the entry.
        }
    }

    private static class Json
    {
        public static T Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
            => JsonSerializer.Deserialize(File.ReadAllText(path), type) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
    }
}

/// <summary>A registry key tree as stored in quarantine.</summary>
public sealed record RegNode(string Name, IReadOnlyList<RegValue> Values, IReadOnlyList<RegNode> Children);

/// <summary>One value: its kind name and its data as text (see <see cref="RegistryTarget.Format"/>).</summary>
public sealed record RegValue(string Name, string Kind, string Data);

/// <summary>A firewall rule's settings, enough to add it again.</summary>
public sealed record FirewallRuleData(
    string Name, string? Description, string? ApplicationName, string? ServiceName, int Protocol, string? LocalPorts, string? RemotePorts,
    string? LocalAddresses, string? RemoteAddresses, string? IcmpTypesAndCodes, int Direction, bool Enabled, int Profiles, int Action,
    string? Grouping, string? InterfaceTypes, bool EdgeTraversal)
{
    public static FirewallRuleData From(dynamic rule) => new(
        (string)rule.Name, rule.Description as string, rule.ApplicationName as string, rule.ServiceName as string, (int)rule.Protocol,
        rule.LocalPorts as string, rule.RemotePorts as string, rule.LocalAddresses as string, rule.RemoteAddresses as string,
        rule.IcmpTypesAndCodes as string, (int)rule.Direction, (bool)rule.Enabled, (int)rule.Profiles, (int)rule.Action,
        rule.Grouping as string, rule.InterfaceTypes as string, (bool)rule.EdgeTraversal);

    public dynamic ToRule()
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule") ?? throw new COMException("Windows Firewall is not available."))!;
        rule.Name = Name;
        rule.Description = Description;
        rule.ApplicationName = ApplicationName;
        rule.ServiceName = ServiceName;
        rule.Protocol = Protocol;

        // Ports and ICMP types are only valid for the protocols that have them (6 TCP, 17 UDP, 1/58 ICMP).
        if (Protocol is 6 or 17)
        {
            if (LocalPorts is not null)
                rule.LocalPorts = LocalPorts;
            if (RemotePorts is not null)
                rule.RemotePorts = RemotePorts;
        }
        else if (Protocol is 1 or 58 && IcmpTypesAndCodes is not null)
        {
            rule.IcmpTypesAndCodes = IcmpTypesAndCodes;
        }

        if (LocalAddresses is not null)
            rule.LocalAddresses = LocalAddresses;
        if (RemoteAddresses is not null)
            rule.RemoteAddresses = RemoteAddresses;

        rule.Direction = Direction;
        rule.Profiles = Profiles;
        rule.Action = Action;
        rule.Grouping = Grouping;

        if (InterfaceTypes is not null)
            rule.InterfaceTypes = InterfaceTypes;

        rule.EdgeTraversal = EdgeTraversal;
        rule.Enabled = Enabled;
        return rule;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegNode))]
[JsonSerializable(typeof(ServiceDefinition))]
[JsonSerializable(typeof(List<FirewallRuleData>))]
internal sealed partial class QuarantineJsonContext : JsonSerializerContext;
