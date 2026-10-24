using Updatr.Core.Catalog;
using Updatr.Core.Operations;

namespace Updatr.Core.Carts;

/// <summary>Browse the catalog.</summary>
/// <param name="Category">Only this category; null for all.</param>
/// <param name="Search">Matches name, id, or description; null for all.</param>
public sealed record CatalogQuery(string? Category = null, string? Search = null);

[Operation("catalog list")]
public sealed class ListCatalog(Func<CancellationToken, Task<AppCatalog>> catalog) : IOperation<CatalogQuery, IReadOnlyList<CatalogApp>>
{
    public async Task<IReadOnlyList<CatalogApp>> RunAsync(CatalogQuery request, IOperationSink sink, CancellationToken cancellationToken)
    {
        AppCatalog apps = await catalog(cancellationToken).ConfigureAwait(false);

        return [.. apps.Apps
            .Where(a => request.Category is null || string.Equals(a.Category, request.Category, StringComparison.OrdinalIgnoreCase))
            .Where(a => request.Search is null
                || a.Name.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
                || a.Id.Contains(request.Search, StringComparison.OrdinalIgnoreCase)
                || (a.Description?.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(a => a.Category, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>Saved carts: names only.</summary>
[Operation("cart list")]
public sealed class ListCarts(CartStore store) : IOperation<bool, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
        => store.ListCartsAsync(cancellationToken);
}

[Operation("cart show")]
public sealed class ShowCart(CartStore store) : IOperation<string, Cart>
{
    public async Task<Cart> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
        => await store.LoadCartAsync(request, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException($"No saved cart named '{request}'.");
}

/// <summary>Builds a cart from apps, templates, and other carts, and saves it. Saving over a name replaces it.</summary>
public sealed record SaveCartRequest(string Name, IReadOnlyList<string> AppIds, IReadOnlyList<string> Templates, IReadOnlyList<string> Carts, Cart? Cart = null);

[Operation("cart save")]
public sealed class SaveCart(TemplateCatalog templates, CartStore store) : IOperation<SaveCartRequest, Cart>
{
    public async Task<Cart> RunAsync(SaveCartRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Cart cart = await CartAssembly.BuildAsync(request.Cart, request.AppIds, request.Templates, request.Carts, templates, store, cancellationToken, request.Name).ConfigureAwait(false);

        // Automatic prerequisites are recomputed at install time; only the real picks are stored.
        cart = cart with { Name = request.Name, Items = [.. cart.Items.Where(i => !i.Automatic)] };
        await store.SaveCartAsync(cart, cancellationToken).ConfigureAwait(false);
        return cart;
    }
}

public sealed record RemoveFromCartRequest(string Name, IReadOnlyList<string> AppIds);

[Operation("cart remove")]
public sealed class RemoveFromCart(CartStore store) : IOperation<RemoveFromCartRequest, Cart>
{
    public async Task<Cart> RunAsync(RemoveFromCartRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        Cart cart = await store.LoadCartAsync(request.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"No saved cart named '{request.Name}'.");
        cart = request.AppIds.Aggregate(cart, CartBuilder.Remove);
        await store.SaveCartAsync(cart, cancellationToken).ConfigureAwait(false);
        return cart;
    }
}

[Operation("cart delete")]
public sealed class DeleteCart(CartStore store) : IOperation<string, bool>
{
    public async Task<bool> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        await store.DeleteCartAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>A template and whether it ships in the catalog (read-only) or is the user's.</summary>
public sealed record TemplateInfo(CatalogTemplate Template, bool BuiltIn);

[Operation("template list")]
public sealed class ListTemplates(TemplateCatalog templates) : IOperation<bool, IReadOnlyList<TemplateInfo>>
{
    public async Task<IReadOnlyList<TemplateInfo>> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
        => [.. (await templates.AllAsync(cancellationToken).ConfigureAwait(false)).Select(t => new TemplateInfo(t.Template, t.BuiltIn))];
}

[Operation("template show")]
public sealed class ShowTemplate(TemplateCatalog templates) : IOperation<string, TemplateInfo>
{
    public async Task<TemplateInfo> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        (CatalogTemplate Template, bool BuiltIn) found = (await templates.AllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(t => string.Equals(t.Template.Id, request, StringComparison.OrdinalIgnoreCase));

        return found.Template is null ? throw new ArgumentException($"No template named '{request}'.") : new TemplateInfo(found.Template, found.BuiltIn);
    }
}

/// <summary>Saves one of the user's templates (new or replacing their own). Built-ins cannot be overwritten.</summary>
public sealed record SaveTemplateRequest(string Id, string Name, string? Description, IReadOnlyList<string> AppIds);

[Operation("template save")]
public sealed class SaveTemplate(Func<CancellationToken, Task<AppCatalog>> catalog, CartStore store) : IOperation<SaveTemplateRequest, CatalogTemplate>
{
    public async Task<CatalogTemplate> RunAsync(SaveTemplateRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        AppCatalog apps = await catalog(cancellationToken).ConfigureAwait(false);
        string id = CartStore.Slug(request.Id);

        if (apps.FindTemplate(id) is not null)
            throw new ArgumentException($"'{id}' is a built-in template. Copy it first (template copy {id}) and edit the copy.");

        string[] unknown = [.. request.AppIds.Where(a => apps.FindApp(a) is null)];

        if (unknown.Length > 0)
            throw new ArgumentException($"Not in the catalog: {string.Join(", ", unknown)}");

        CatalogTemplate template = new() { Id = id, Name = request.Name, Description = request.Description, Items = [.. request.AppIds.Distinct(StringComparer.Ordinal)] };
        await store.SaveUserTemplateAsync(template, cancellationToken).ConfigureAwait(false);
        return template;
    }
}

/// <summary>Copies any template (usually a built-in) to an editable one of the user's.</summary>
public sealed record CopyTemplateRequest(string From, string? NewId = null);

[Operation("template copy")]
public sealed class CopyTemplate(TemplateCatalog templates, CartStore store) : IOperation<CopyTemplateRequest, CatalogTemplate>
{
    public async Task<CatalogTemplate> RunAsync(CopyTemplateRequest request, IOperationSink sink, CancellationToken cancellationToken)
    {
        CatalogTemplate source = await templates.FindAsync(request.From, cancellationToken).ConfigureAwait(false)
            ?? throw new ArgumentException($"No template named '{request.From}'.");

        CatalogTemplate copy = source with { Id = CartStore.Slug(request.NewId ?? $"{source.Id}-mine"), Name = $"{source.Name} (mine)" };
        await store.SaveUserTemplateAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy;
    }
}

[Operation("template delete")]
public sealed class DeleteTemplate(Func<CancellationToken, Task<AppCatalog>> catalog, CartStore store) : IOperation<string, bool>
{
    public async Task<bool> RunAsync(string request, IOperationSink sink, CancellationToken cancellationToken)
    {
        if ((await catalog(cancellationToken).ConfigureAwait(false)).FindTemplate(request) is not null)
            throw new ArgumentException($"'{request}' is built in and cannot be deleted. Your copies can.");

        await store.DeleteUserTemplateAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
