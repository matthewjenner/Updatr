using Updatr.Core.Catalog;
using Updatr.Platform.Net;

namespace Updatr.Platform.Catalog;

/// <summary>The catalog straight from the repository on GitHub - the source of truth.</summary>
public sealed class RemoteCatalogOrigin(HttpClient http, string baseUrl = UpdatrHttp.RawCatalogBase) : ICatalogOrigin
{
    /// <summary>Whole-set deadline. Short: on a slow link the cached copy is better than waiting.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public CatalogOriginKind Kind => CatalogOriginKind.Remote;

    public async Task<CatalogFileSet?> ReadAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = UpdatrHttp.Deadline(Timeout, cancellationToken);

        try
        {
            string[] bodies = await Task.WhenAll(CatalogFiles.All.Select(async file =>
            {
                using HttpResponseMessage response = await http.GetAsync(baseUrl + file, deadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            })).ConfigureAwait(false);

            Dictionary<string, string> files = new(StringComparer.Ordinal);

            for (int i = 0; i < bodies.Length; i++)
                files[CatalogFiles.All[i]] = bodies[i];

            return new CatalogFileSet(files, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"GitHub did not answer within {Timeout.TotalSeconds:0} s");
        }
    }
}
