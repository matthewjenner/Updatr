using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Updatr.Core.Catalog;

/// <summary>Whether a debloat item is applied by default or only when picked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DebloatGroup>))]
public enum DebloatGroup
{
    /// <summary>Ads and junk that should never have shipped, plus baseline privacy. Applied by default.</summary>
    [JsonStringEnumMemberName("recommended")]
    Recommended,

    /// <summary>Real features some people use (Edge, OneDrive, Copilot, Xbox, ...). Only when picked.</summary>
    [JsonStringEnumMemberName("optional")]
    Optional,
}

/// <summary>
/// One debloat item: something the user sees, picks, and can revert as a unit ("Start menu
/// suggestions", "Copilot"). Its actions are the concrete machine changes.
/// </summary>
[Description("One debloat item: a named, revertible set of changes.")]
public sealed record DebloatItem
{
    [Description("Unique id: lowercase letters/digits joined by single hyphens. Shown in the CLI and kept in history - do not rename.")]
    public required string Id { get; init; }

    [Description("recommended (applied by default) or optional (only when picked).")]
    public required DebloatGroup Group { get; init; }

    [Description("Heading it is listed under, e.g. \"Ads and suggestions\".")]
    public required string Category { get; init; }

    [Description("Short name shown in the list.")]
    public required string Title { get; init; }

    [Description("What it changes and why, in a sentence or two.")]
    public required string Description { get; init; }

    [Description("Shown before applying when the change has a cost (\"Game Pass needs the Xbox app\").")]
    public string? Warning { get; init; }

    [Description("Lowest Windows build it applies to (22000 = Windows 11). Omit for every supported build.")]
    public int? MinBuild { get; init; }

    [Description("The changes, applied in order and reverted in reverse.")]
    public required IReadOnlyList<DebloatAction> Actions { get; init; }
}

/// <summary>One concrete change to the machine.</summary>
/// <remarks>The <c>$type</c> discriminators are user-visible and stored in backups - do not rename them.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RegistryAction), "registry")]
[JsonDerivedType(typeof(AppxAction), "appx")]
[JsonDerivedType(typeof(ServiceAction), "service")]
[JsonDerivedType(typeof(ScheduledTaskAction), "scheduledTask")]
public abstract record DebloatAction
{
    /// <summary>One line for logs and the CLI.</summary>
    [JsonIgnore]
    public string Describe => this switch
    {
        RegistryAction r => $"{(r.Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{r.Key}\\{r.Name}"
            + (r.Delete ? " (remove)" : r.Dword is { } d ? $" = {d}" : $" = \"{r.String}\""),
        AppxAction a => $"remove app {a.Name}",
        ServiceAction s => $"service {s.Name} -> {s.StartType.ToString().ToLowerInvariant()}",
        ScheduledTaskAction t => $"disable task {t.Path}",
        _ => GetType().Name,
    };
}

/// <summary>Registry roots a debloat rule may write. JSON names are user-visible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegistryHive>))]
public enum RegistryHive
{
    /// <summary>Machine-wide; needs administrator access to write.</summary>
    [JsonStringEnumMemberName("hklm")]
    LocalMachine,

    /// <summary>The signed-in user; written without elevation.</summary>
    [JsonStringEnumMemberName("hkcu")]
    CurrentUser,
}

[Description("Set or remove one registry value. Set exactly one of dword, string, or delete.")]
public sealed record RegistryAction : DebloatAction
{
    [Description("hklm or hkcu.")]
    public required RegistryHive Hive { get; init; }

    [Description("Key path under the hive, e.g. Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo.")]
    public required string Key { get; init; }

    [Description("Value name.")]
    public required string Name { get; init; }

    [Description("Set a REG_DWORD to this number.")]
    public int? Dword { get; init; }

    [Description("Set a REG_SZ to this text.")]
    public string? String { get; init; }

    [Description("Remove the value instead of setting it.")]
    public bool Delete { get; init; }
}

[Description("Remove a Microsoft Store (Appx) app for the current user, and from the image so new users do not get it.")]
public sealed record AppxAction : DebloatAction
{
    [Description("Package name wildcard (* and ?), e.g. Microsoft.BingNews or king.com.*. Case-insensitive.")]
    public required string Name { get; init; }

    [Description("Microsoft Store product id used to reinstall it on revert, e.g. 9WZDNCRFHVFW. Omit when the Store no longer offers it.")]
    public string? StoreId { get; init; }

    // A negative flag on purpose: the source-generated deserializer assigns false to an omitted
    // bool, so a "= true" default would be silently lost.
    [Description("Leave it in the Windows image, so only the current user loses it (skips the administrator step).")]
    public bool KeepProvisioned { get; init; }
}

/// <summary>Start types a debloat rule may set. JSON names are user-visible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServiceStartType>))]
public enum ServiceStartType
{
    [JsonStringEnumMemberName("disabled")]
    Disabled,

    [JsonStringEnumMemberName("manual")]
    Manual,
}

[Description("Change a Windows service's start type (and stop it when disabling). Needs administrator access.")]
public sealed record ServiceAction : DebloatAction
{
    [Description("Service name (not the display name), e.g. DiagTrack.")]
    public required string Name { get; init; }

    [Description("disabled or manual.")]
    public required ServiceStartType StartType { get; init; }
}

[Description("Disable a scheduled task. Needs administrator access.")]
public sealed record ScheduledTaskAction : DebloatAction
{
    [Description("Full task path, e.g. \\Microsoft\\Windows\\Feedback\\Siuf\\DmClient.")]
    public required string Path { get; init; }
}
