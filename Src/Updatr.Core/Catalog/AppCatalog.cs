namespace Updatr.Core.Catalog;

/// <summary>A loaded, validated catalog: apps, templates, hold rules, and debloat items from one consistent set of files.</summary>
public sealed class AppCatalog
{
    private readonly Dictionary<string, CatalogApp> _apps;
    private readonly Dictionary<string, CatalogTemplate> _templates;

    public AppCatalog(IReadOnlyList<CatalogApp> apps, IReadOnlyList<CatalogTemplate> templates, IReadOnlyList<HoldRule> holds, DebloatFile? debloat = null)
    {
        Apps = apps;
        Templates = templates;
        Holds = holds;
        Debloat = debloat?.Items ?? [];
        DebloatKeep = debloat?.Keep ?? [];
        _apps = apps.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _templates = templates.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CatalogApp> Apps { get; }

    public IReadOnlyList<CatalogTemplate> Templates { get; }

    public IReadOnlyList<HoldRule> Holds { get; }

    public IReadOnlyList<DebloatItem> Debloat { get; }

    /// <summary>Store app name patterns never listed as unrecognized.</summary>
    public IReadOnlyList<string> DebloatKeep { get; }

    public CatalogApp? FindApp(string id) => _apps.GetValueOrDefault(id);

    public CatalogTemplate? FindTemplate(string id) => _templates.GetValueOrDefault(id);
}
