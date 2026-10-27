namespace Updatr.Core.Catalog;

/// <summary>
/// The Install screen's sections ("aisles"): catalog categories in shopping order, with display
/// names. A category the list does not know still works - it shows under its own name, after the
/// known ones - so the catalog can add one before every Updatr in the field knows its title.
/// </summary>
public static class CatalogSections
{
    /// <summary>One section.</summary>
    /// <param name="Apart">Shown separately from the rest and left out of "All apps" (VS Code extensions would take over the list).</param>
    public sealed record Section(string Key, string Title, bool Apart = false);

    /// <summary>Apps whose only source is a VS Code extension land here whatever their category says.</summary>
    public const string VsCodeExtensions = "vscode";

    public static IReadOnlyList<Section> Known { get; } =
    [
        new("browsers", "Web browsers"),
        new("messaging", "Messaging & meetings"),
        new("media", "Music & video"),
        new("editing", "Recording & editing"),
        new("imaging", "Photos & design"),
        new("3d", "3D & printing"),
        new("documents", "Office & PDF"),
        new("productivity", "Notes & productivity"),
        new("ai", "AI"),
        new("security", "Security & passwords"),
        new("storage", "Cloud storage"),
        new("transfer", "File transfer"),
        new("remote", "Remote access & VPN"),
        new("utilities", "System utilities"),
        new("hardware", "Hardware & drivers"),
        new("gaming", "Gaming"),
        new("dev", "Developer tools"),
        new("runtimes", "Runtimes"),
        new(VsCodeExtensions, "VS Code extensions", Apart: true),
    ];

    /// <summary>The section an app is shown in.</summary>
    public static string SectionOf(CatalogApp app)
        => app.Sources.Count > 0 && app.Sources.All(s => s is VsCodeExtensionSourceRef) ? VsCodeExtensions : app.Category.Trim().ToLowerInvariant();

    public static Section Describe(string key)
        => Known.FirstOrDefault(s => s.Key == key)
            ?? new Section(key, key.Length == 0 ? "Other" : char.ToUpperInvariant(key[0]) + key[1..]);

    /// <summary>Sort position: known sections in list order, unknown ones next, apart ones last.</summary>
    public static int Order(string key)
    {
        int index = Known.ToList().FindIndex(s => s.Key == key);
        return index < 0 ? Known.Count : Known[index].Apart ? Known.Count + 1 + index : index;
    }
}
