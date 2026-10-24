using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;

namespace Updatr.Core.Carts;

/// <summary>One app in a cart.</summary>
[Description("An app queued for install.")]
public sealed record CartItem
{
    [Description("Catalog app id.")]
    public required string AppId { get; init; }

    [Description("Which of the app's sources to use (its source id: winget, npm, ...). Omit for the app's first source.")]
    public string? Source { get; init; }

    [Description("What put it in the cart: a template id, a cart name, or \"you\".")]
    public string? AddedBy { get; init; }

    [Description("Added automatically as a prerequisite of other cart items (dropped when they are).")]
    public bool Automatic { get; init; }

    [Description("The cart items that need this one (prerequisites only).")]
    public IReadOnlyList<string> RequiredBy { get; init => field = value ?? []; } = [];
}

/// <summary>A named list of apps to install - built from picks, templates, and other carts.</summary>
[Description("An Updatr cart: apps to install together.")]
public sealed record Cart
{
    public int SchemaVersion { get; init; } = 1;

    public required string Name { get; init; }

    public IReadOnlyList<CartItem> Items { get; init => field = value ?? []; } = [];

    public static Cart Empty(string name = "Cart") => new() { Name = name };
}

/// <summary>Saved carts and user templates in the portable data folder.</summary>
public sealed partial class CartStore(IDataStore store)
{
    public const string CartFolder = "carts";
    public const string TemplateFolder = "templates";

    public async Task<IReadOnlyList<string>> ListCartsAsync(CancellationToken cancellationToken)
        => [.. (await store.ListAsync(CartFolder, cancellationToken).ConfigureAwait(false))
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f))];

    public async Task<Cart?> LoadCartAsync(string name, CancellationToken cancellationToken)
    {
        string? json = await store.ReadTextAsync($"{CartFolder}/{Slug(name)}.json", cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize(json, CartJsonContext.Default.Cart);
    }

    public Task SaveCartAsync(Cart cart, CancellationToken cancellationToken)
        => store.WriteTextAsync($"{CartFolder}/{Slug(cart.Name)}.json", JsonSerializer.Serialize(cart, CartJsonContext.Default.Cart), cancellationToken);

    public Task DeleteCartAsync(string name, CancellationToken cancellationToken)
        => store.DeleteAsync($"{CartFolder}/{Slug(name)}.json", cancellationToken);

    /// <summary>The user's own templates (built-ins come from the catalog).</summary>
    public async Task<IReadOnlyList<CatalogTemplate>> LoadUserTemplatesAsync(CancellationToken cancellationToken)
    {
        List<CatalogTemplate> templates = [];

        foreach (string file in await store.ListAsync(TemplateFolder, cancellationToken).ConfigureAwait(false))
        {
            string? json = await store.ReadTextAsync(file, cancellationToken).ConfigureAwait(false);

            if (json is null)
                continue;

            try
            {
                if (JsonSerializer.Deserialize(json, CartJsonContext.Default.CatalogTemplate) is { } template)
                    templates.Add(template);
            }
            catch (JsonException)
            {
                // A broken hand-edit: skip that template, keep the rest.
            }
        }

        return templates;
    }

    public Task SaveUserTemplateAsync(CatalogTemplate template, CancellationToken cancellationToken)
        => store.WriteTextAsync($"{TemplateFolder}/{template.Id}.json", JsonSerializer.Serialize(template, CartJsonContext.Default.CatalogTemplate), cancellationToken);

    public Task DeleteUserTemplateAsync(string id, CancellationToken cancellationToken)
        => store.DeleteAsync($"{TemplateFolder}/{Slug(id)}.json", cancellationToken);

    /// <summary>A file- and id-safe form of a name: lowercase letters, digits, single hyphens.</summary>
    public static string Slug(string name)
    {
        string slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "cart" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlug();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(Cart))]
[JsonSerializable(typeof(CatalogTemplate))]
internal sealed partial class CartJsonContext : JsonSerializerContext;
