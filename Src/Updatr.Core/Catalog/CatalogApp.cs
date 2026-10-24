using System.ComponentModel;

namespace Updatr.Core.Catalog;

/// <summary>An installable app in the catalog.</summary>
[Description("An installable app.")]
public sealed record CatalogApp
{
    [Description("Stable catalog id: lowercase letters, digits, and single hyphens. Templates and carts refer to apps by this id.")]
    public required string Id { get; init; }

    [Description("Display name.")]
    public required string Name { get; init; }

    [Description("Category the Install screen groups by, e.g. dev, browsers, media, gaming, utilities.")]
    public required string Category { get; init; }

    [Description("One-line description shown in the picker.")]
    public string? Description { get; init; }

    [Description("Project or vendor home page (https).")]
    public string? Homepage { get; init; }

    [Description("Ways to install the app, most preferred first. At least one.")]
    public required IReadOnlyList<PackageSourceRef> Sources { get; init; }

    [Description("Tools this app puts on the machine (e.g. node provides npm). Used to add prerequisites to a cart automatically.")]
    public IReadOnlyList<string> Provides { get; init => field = value ?? []; } = [];

    [Description("Other catalog app ids this app needs, beyond what its sources imply.")]
    public IReadOnlyList<string> Requires { get; init => field = value ?? []; } = [];
}

/// <summary>A named set of apps that can be added to a cart in one go.</summary>
[Description("A named set of apps.")]
public sealed record CatalogTemplate
{
    [Description("Stable template id, same rules as app ids.")]
    public required string Id { get; init; }

    [Description("Display name.")]
    public required string Name { get; init; }

    [Description("What the template is for.")]
    public string? Description { get; init; }

    [Description("Catalog app ids in this template.")]
    public required IReadOnlyList<string> Items { get; init; }
}
