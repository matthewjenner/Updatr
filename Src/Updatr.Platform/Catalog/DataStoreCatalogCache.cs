using System.Globalization;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;

namespace Updatr.Platform.Catalog;

/// <summary>
/// The last good remote catalog, saved in <c>Updatr.data\catalog-cache\</c> so an offline machine
/// still gets a recent catalog rather than the one built into the exe.
/// </summary>
/// <remarks>
/// The <c>fetched.txt</c> marker is written last and read first: a set interrupted mid-write (drive
/// pulled) has no marker for the new files and is treated as absent, never as half old, half new.
/// </remarks>
public sealed class DataStoreCatalogCache(IDataStore store) : ICatalogCache
{
    public const string Folder = "catalog-cache";
    private const string Marker = Folder + "/fetched.txt";

    public CatalogOriginKind Kind => CatalogOriginKind.Cache;

    public async Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken)
    {
        string? marker = await store.ReadTextAsync(Marker, cancellationToken).ConfigureAwait(false);

        if (marker is null)
            return null;

        Dictionary<string, string> files = new(StringComparer.Ordinal);

        foreach (string file in CatalogFiles.All)
        {
            string? text = await store.ReadTextAsync($"{Folder}/{file}", cancellationToken).ConfigureAwait(false);

            if (text is null)
                return null;

            files[file] = text;
        }

        DateTimeOffset? fetched = DateTimeOffset.TryParse(marker.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;

        return new CatalogFileSet(files, fetched);
    }

    public async Task WriteAsync(CatalogFileSet set, CancellationToken cancellationToken)
    {
        await store.DeleteAsync(Marker, cancellationToken).ConfigureAwait(false);

        foreach ((string file, string text) in set.Files)
            await store.WriteTextAsync($"{Folder}/{file}", text, cancellationToken).ConfigureAwait(false);

        string stamp = (set.Timestamp ?? DateTimeOffset.Now).ToString("O", CultureInfo.InvariantCulture);
        await store.WriteTextAsync(Marker, stamp, cancellationToken).ConfigureAwait(false);
    }
}
