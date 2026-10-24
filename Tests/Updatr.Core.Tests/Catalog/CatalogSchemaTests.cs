using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

/// <summary>
/// catalog/schema/*.schema.json give editors completion and validation while hand-editing the
/// catalog. They are generated from the C# models, so they cannot drift: this test regenerates
/// them and fails if the checked-in copies differ.
/// </summary>
/// <remarks>
/// After changing a catalog model, regenerate with:
/// <code>UPDATR_UPDATE_SCHEMAS=1 dotnet test</code>
/// then review and commit the schema diff alongside the model change.
/// </remarks>
public sealed class CatalogSchemaTests
{
    public static TheoryData<string, Type> Files => new()
    {
        { "apps.schema.json", typeof(AppsFile) },
        { "templates.schema.json", typeof(TemplatesFile) },
        { "holds.schema.json", typeof(HoldsFile) },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void CheckedInSchemaMatchesTheModels(string fileName, Type fileType)
    {
        string path = RepositoryPaths.Resolve(Path.Combine("catalog", "schema", fileName));
        string expected = Generate(fileType);

        if (Environment.GetEnvironmentVariable("UPDATR_UPDATE_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }

        File.Exists(path).Should().BeTrue($"{fileName} is missing; run UPDATR_UPDATE_SCHEMAS=1 dotnet test");
        Normalize(File.ReadAllText(path)).Should().Be(
            Normalize(expected),
            $"catalog/schema/{fileName} is stale; run UPDATR_UPDATE_SCHEMAS=1 dotnet test and commit the result");
    }

    private static string Generate(Type fileType)
    {
        JsonSchemaExporterOptions exporter = new()
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = AddDescription,
        };

        JsonNode schema = CatalogJson.Options.GetJsonSchemaAsNode(fileType, exporter);
        JsonObject root = new() { ["$schema"] = "https://json-schema.org/draft/2020-12/schema" };

        foreach ((string key, JsonNode? value) in schema.AsObject().ToList())
        {
            schema.AsObject().Remove(key);
            root[key] = value;
        }

        // Relaxed escaping keeps quotes in descriptions readable ("winget", not "winget").
        // The output is a file in the repository, never HTML, so the stricter default buys nothing.
        JsonSerializerOptions write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        return root.ToJsonString(write) + "\n";
    }

    private static JsonNode AddDescription(JsonSchemaExporterContext context, JsonNode schema)
    {
        ICustomAttributeProvider? provider = context.PropertyInfo is not null
            ? context.PropertyInfo.AttributeProvider
            : context.TypeInfo.Type;

        DescriptionAttribute? description = provider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault();

        if (description is not null && schema is JsonObject obj && !obj.ContainsKey("description"))
            obj.Insert(0, "description", description.Description);

        return schema;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
