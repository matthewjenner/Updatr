namespace Updatr.Core.Catalog;

/// <summary>Where a catalog set came from, in the order the loader tries them.</summary>
public enum CatalogOriginKind
{
    /// <summary>Latest from the repository on GitHub.</summary>
    Remote,

    /// <summary>The last good remote copy, saved in the portable data folder.</summary>
    Cache,

    /// <summary>The copy compiled into this build. Always present and valid (tested in CI).</summary>
    Embedded,
}

/// <summary>The raw text of every catalog file, keyed by file name (see <see cref="CatalogFiles"/>).</summary>
/// <param name="Files">File name to JSON text.</param>
/// <param name="Timestamp">When this set was fetched, if known.</param>
public sealed record CatalogFileSet(IReadOnlyDictionary<string, string> Files, DateTimeOffset? Timestamp);

/// <summary>A place a catalog set can be read from.</summary>
public interface ICatalogOrigin
{
    CatalogOriginKind Kind { get; }

    /// <summary>
    /// Reads the set, or returns null when this origin simply has nothing (no cache yet). Throwing
    /// is fine for real failures (timeout, network error); the loader records it and moves on.
    /// </summary>
    Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A catalog origin that can also be written: the local cache of the last good remote set.</summary>
public interface ICatalogCache : ICatalogOrigin
{
    Task WriteAsync(CatalogFileSet set, CancellationToken cancellationToken);
}

/// <summary>The catalog files compiled into Updatr.Core as embedded resources.</summary>
public sealed class EmbeddedCatalogOrigin : ICatalogOrigin
{
    /// <summary>Resource name prefix; see the EmbeddedResource item in Updatr.Core.csproj.</summary>
    public const string ResourcePrefix = "Updatr.Catalog.";

    public CatalogOriginKind Kind => CatalogOriginKind.Embedded;

    public Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        System.Reflection.Assembly assembly = typeof(EmbeddedCatalogOrigin).Assembly;

        foreach (string name in CatalogFiles.All)
        {
            using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + name);

            if (stream is null)
                continue;

            using StreamReader reader = new(stream);
            files[name] = reader.ReadToEnd();
        }

        return Task.FromResult<CatalogFileSet?>(new CatalogFileSet(files, Timestamp: null));
    }
}
