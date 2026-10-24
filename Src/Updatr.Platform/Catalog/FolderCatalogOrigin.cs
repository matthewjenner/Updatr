using Updatr.Core.Catalog;

namespace Updatr.Platform.Catalog;

/// <summary>
/// The catalog read from a local folder - for developing the catalog itself. Set
/// <c>UPDATR_CATALOG_DIR</c> to a repo's <c>catalog/</c> folder and Updatr uses it ahead of GitHub,
/// so edits can be tried before they are pushed (and go live for every Updatr in the field).
/// </summary>
public sealed class FolderCatalogOrigin(string folder) : ICatalogOrigin
{
    public const string EnvironmentVariable = "UPDATR_CATALOG_DIR";

    /// <summary>Reported as Embedded so the loader never copies a dev folder into the drive's cache.</summary>
    public CatalogOriginKind Kind => CatalogOriginKind.Embedded;

    public string Folder => folder;

    /// <summary>The override from the environment, or null when it is not set.</summary>
    public static FolderCatalogOrigin? FromEnvironment()
        => Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } dir ? new FolderCatalogOrigin(dir) : null;

    public async Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach (string name in CatalogFiles.All)
        {
            string path = Path.Combine(folder, name);

            if (!File.Exists(path))
                return null;

            files[name] = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return new CatalogFileSet(files, File.GetLastWriteTime(Path.Combine(folder, CatalogFiles.Apps)));
    }
}
