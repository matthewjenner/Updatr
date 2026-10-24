using Updatr.Core.Catalog;

namespace Updatr.Core.Carts;

/// <summary>A cart item ready to install: its app, the chosen source, and why it is there.</summary>
public sealed record PlannedItem(CatalogApp App, PackageSourceRef Source, CartItem Item);

/// <summary>A cart turned into an install order, plus anything that cannot be satisfied.</summary>
/// <param name="Items">Install order: every prerequisite before what needs it.</param>
/// <param name="Problems">Unknown apps, or tools nothing in the catalog provides.</param>
public sealed record CartPlan(IReadOnlyList<PlannedItem> Items, IReadOnlyList<string> Problems);

/// <summary>
/// Building and resolving carts. Pure: everything it needs about the machine comes in as data.
/// </summary>
public static class CartBuilder
{
    public const string AddedByYou = "you";

    /// <summary>Adds an app; already present means unchanged (not a duplicate).</summary>
    public static Cart Add(Cart cart, string appId, string addedBy = AddedByYou, string? source = null)
    {
        CartItem? existing = cart.Items.FirstOrDefault(i => i.AppId == appId);

        if (existing is null)
            return cart with { Items = [.. cart.Items, new CartItem { AppId = appId, AddedBy = addedBy, Source = source }] };

        // Picking something that was only there as a prerequisite makes it a real pick.
        return existing.Automatic
            ? Replace(cart, existing with { Automatic = false, AddedBy = addedBy, RequiredBy = [] })
            : cart;
    }

    /// <summary>Adds every app of a template; duplicates merge.</summary>
    public static Cart AddTemplate(Cart cart, CatalogTemplate template)
        => template.Items.Aggregate(cart, (c, appId) => Add(c, appId, template.Id));

    /// <summary>Union of two carts: the first cart's choices win where both have an app.</summary>
    public static Cart Merge(Cart cart, Cart other)
        => other.Items.Where(i => !i.Automatic).Aggregate(cart, (c, item) => Add(c, item.AppId, item.AddedBy ?? other.Name, item.Source));

    /// <summary>Removes an app. Prerequisites only it needed go on the next <see cref="Resolve"/>.</summary>
    public static Cart Remove(Cart cart, string appId)
        => cart with { Items = [.. cart.Items.Where(i => i.AppId != appId)] };

    /// <summary>Chooses which of an app's sources to install from.</summary>
    public static Cart ChooseSource(Cart cart, string appId, string sourceId)
        => cart.Items.FirstOrDefault(i => i.AppId == appId) is { } item ? Replace(cart, item with { Source = sourceId }) : cart;

    /// <summary>
    /// Adds missing prerequisites and orders the cart for install.
    /// </summary>
    /// <param name="cart">The cart; its automatic items are recomputed from scratch.</param>
    /// <param name="catalog">Where apps and their providers come from.</param>
    /// <param name="toolsPresent">Tools already on this machine ("npm", "code", ...).</param>
    /// <remarks>
    /// A source's required tool (npm for an npm package) is satisfied by the machine, by another
    /// cart item that provides it, or by adding the catalog app that provides it - marked
    /// automatic, with "required by". Explicit <c>requires</c> are added the same way. The result
    /// lists prerequisites first. Automatic items nothing needs any more are dropped.
    /// </remarks>
    public static (Cart Cart, CartPlan Plan) Resolve(Cart cart, AppCatalog catalog, IReadOnlySet<string> toolsPresent)
    {
        List<string> problems = [];
        Dictionary<string, CartItem> items = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> requiredBy = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> dependsOn = new(StringComparer.Ordinal);
        Queue<string> pending = new();

        foreach (CartItem item in cart.Items.Where(i => !i.Automatic))
        {
            items[item.AppId] = item;
            pending.Enqueue(item.AppId);
        }

        while (pending.Count > 0)
        {
            string appId = pending.Dequeue();
            CatalogApp? app = catalog.FindApp(appId);

            if (app is null)
            {
                problems.Add($"'{appId}' is not in the catalog");
                items.Remove(appId);
                continue;
            }

            PackageSourceRef source = SourceFor(app, items[appId].Source);
            List<string> needs = [.. app.Requires];

            if (source.RequiredTool is { } tool && tool != "winget" && !toolsPresent.Contains(tool))
            {
                string? provider = items.Keys.FirstOrDefault(id => catalog.FindApp(id)?.Provides.Contains(tool) == true)
                    ?? catalog.Apps.FirstOrDefault(a => a.Provides.Contains(tool))?.Id;

                if (provider is null)
                    problems.Add($"{app.Name} needs {tool}, and nothing in the catalog provides it");
                else
                    needs.Add(provider);
            }

            dependsOn[appId] = needs;

            foreach (string need in needs)
            {
                if (!requiredBy.TryGetValue(need, out HashSet<string>? who))
                    requiredBy[need] = who = new HashSet<string>(StringComparer.Ordinal);

                who.Add(appId);

                if (!items.ContainsKey(need))
                {
                    items[need] = new CartItem { AppId = need, AddedBy = "prerequisite", Automatic = true };
                    pending.Enqueue(need);
                }
            }
        }

        // Prerequisites first (depth-first post-order), keeping the user's order otherwise.
        List<PlannedItem> ordered = [];
        HashSet<string> placed = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);

        void Place(string appId)
        {
            if (placed.Contains(appId) || !items.TryGetValue(appId, out CartItem? item) || !visiting.Add(appId))
                return; // Already placed, unknown, or a requires-cycle (placed where first met).

            foreach (string need in dependsOn.GetValueOrDefault(appId) ?? [])
                Place(need);

            CatalogApp app = catalog.FindApp(appId)!;
            CartItem final = item.Automatic
                ? item with { RequiredBy = [.. requiredBy.GetValueOrDefault(appId)?.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>()] }
                : item with { RequiredBy = [] };

            ordered.Add(new PlannedItem(app, SourceFor(app, final.Source), final));
            placed.Add(appId);
        }

        foreach (string appId in cart.Items.Where(i => !i.Automatic).Select(i => i.AppId).Concat(items.Keys))
            Place(appId);

        Cart resolved = cart with { Items = [.. ordered.Select(p => p.Item)] };
        return (resolved, new CartPlan(ordered, problems));
    }

    /// <summary>The source an item asked for, or the app's first.</summary>
    public static PackageSourceRef SourceFor(CatalogApp app, string? sourceId)
        => (sourceId is null ? null : app.Sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
            ?? app.Sources[0];

    private static Cart Replace(Cart cart, CartItem item)
        => cart with { Items = [.. cart.Items.Select(i => i.AppId == item.AppId ? item : i)] };
}
