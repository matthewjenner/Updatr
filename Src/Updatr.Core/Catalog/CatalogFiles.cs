using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Updatr.Core.Catalog;

/// <summary>Names and schema version of the files that make up the catalog.</summary>
public static class CatalogFiles
{
    public const string Apps = "apps.json";
    public const string Templates = "templates.json";
    public const string Holds = "holds.json";

    /// <summary>Every file in a catalog set. An origin must supply all of them to be used.</summary>
    public static IReadOnlyList<string> All { get; } = [Apps, Templates, Holds];

    /// <summary>
    /// The newest <c>schemaVersion</c> this build understands. Bump only for breaking changes
    /// (renamed or retyped fields, new required fields). Adding an optional field is not breaking:
    /// older builds ignore unknown properties.
    /// </summary>
    public const int SchemaVersion = 1;
}

/// <summary>Members every catalog file shares.</summary>
public abstract record CatalogFileBase
{
    [JsonPropertyName("$schema")]
    [Description("Editor hint pointing at this file's JSON schema. Ignored by Updatr.")]
    public string? Schema { get; init; }

    [Description("Catalog schema version. Updatr ignores a catalog newer than it understands and falls back to its cached or built-in copy.")]
    public required int SchemaVersion { get; init; }
}

[Description("Updatr catalog: installable apps.")]
public sealed record AppsFile : CatalogFileBase
{
    public required IReadOnlyList<CatalogApp> Apps { get; init; }
}

[Description("Updatr catalog: templates (named sets of apps).")]
public sealed record TemplatesFile : CatalogFileBase
{
    public required IReadOnlyList<CatalogTemplate> Templates { get; init; }
}

[Description("Updatr catalog: hold rules for version-sensitive packages.")]
public sealed record HoldsFile : CatalogFileBase
{
    public required IReadOnlyList<HoldRule> Holds { get; init; }
}
