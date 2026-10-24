using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

/// <summary>Builders for catalog test data.</summary>
internal static class TestCatalog
{
    /// <summary>The repository's own catalog/*.json, read from disk.</summary>
    public static CatalogFileSet RepositoryFiles()
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach (string name in CatalogFiles.All)
            files[name] = File.ReadAllText(RepositoryPaths.Resolve(Path.Combine("catalog", name)));

        return new CatalogFileSet(files, Timestamp: null);
    }

    /// <summary>The repository's catalog, parsed strictly. Throws with every issue if it is invalid.</summary>
    public static AppCatalog LoadRepositoryCatalog()
    {
        CatalogParseResult result = CatalogLoader.Parse(RepositoryFiles(), CatalogJson.StrictOptions);

        return result.Catalog
            ?? throw new InvalidOperationException("catalog/ is invalid:\n  " + string.Join("\n  ", result.Issues));
    }

    /// <summary>A small valid set, with optional overrides of individual files.</summary>
    public static CatalogFileSet Minimal(string? apps = null, string? templates = null, string? holds = null, DateTimeOffset? timestamp = null)
        => new(
            new Dictionary<string, string>
            {
                [CatalogFiles.Apps] = apps ?? """
                    { "schemaVersion": 1, "apps": [
                        { "id": "git", "name": "Git", "category": "dev", "sources": [ { "$type": "winget", "id": "Git.Git" } ] }
                    ] }
                    """,
                [CatalogFiles.Templates] = templates ?? """
                    { "schemaVersion": 1, "templates": [ { "id": "dev", "name": "Dev", "items": [ "git" ] } ] }
                    """,
                [CatalogFiles.Holds] = holds ?? """
                    { "schemaVersion": 1, "holds": [] }
                    """,
            },
            timestamp);

    public static string AppsJson(string appsArrayBody)
        => $$"""{ "schemaVersion": 1, "apps": [ {{appsArrayBody}} ] }""";
}

/// <summary>A catalog origin backed by a fixed value, a thrown exception, or nothing.</summary>
internal sealed class FakeOrigin(CatalogOriginKind kind, Func<CatalogFileSet?> read) : ICatalogOrigin
{
    public CatalogOriginKind Kind => kind;

    public int Reads { get; private set; }

    public Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(read());
    }
}

/// <summary>An in-memory cache that records writes; optionally throws on write.</summary>
internal sealed class FakeCache(CatalogFileSet? stored = null, Exception? writeFailure = null) : ICatalogCache
{
    public CatalogFileSet? Stored { get; private set; } = stored;

    public CatalogOriginKind Kind => CatalogOriginKind.Cache;

    public Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task WriteAsync(CatalogFileSet set, CancellationToken cancellationToken)
    {
        if (writeFailure is not null)
            throw writeFailure;

        Stored = set;
        return Task.CompletedTask;
    }
}
