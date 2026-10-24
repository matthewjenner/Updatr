using System.Text.Json;
using System.Text.Json.Serialization;

namespace Updatr.Core.Catalog;

/// <summary>Serializer settings for catalog files and anything shaped like them (user carts, templates).</summary>
/// <remarks>
/// Model rule: an optional property with a default must coerce null in its setter
/// (<c>{ get; init => field = value ?? []; } = [];</c>). The source-generated deserializer assigns
/// every init property, passing null for a field the JSON omits, which would otherwise wipe the
/// default and crash the first caller that enumerates it.
/// </remarks>
public static class CatalogJson
{
    /// <summary>
    /// Runtime reading: tolerant. Unknown properties are ignored so an older Updatr can read a
    /// newer catalog that only added optional fields. <c>$type</c> may appear anywhere in an object
    /// because people hand-edit these files.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(CatalogJsonContext.Default.Options);

    /// <summary>
    /// Strict reading, for the repository's own catalog in tests: an unknown property is a typo
    /// and fails the build.
    /// </summary>
    public static JsonSerializerOptions StrictOptions { get; } = new(CatalogJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(AppsFile))]
[JsonSerializable(typeof(TemplatesFile))]
[JsonSerializable(typeof(HoldsFile))]
[JsonSerializable(typeof(DebloatFile))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
