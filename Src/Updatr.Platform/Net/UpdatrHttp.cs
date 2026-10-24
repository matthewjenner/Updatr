using System.Net;
using System.Net.Http.Headers;
using Updatr.Core;

namespace Updatr.Platform.Net;

/// <summary>The one <see cref="HttpClient"/> Updatr uses, and the URLs it talks to.</summary>
/// <remarks>
/// Uses the system proxy settings (the .NET default), so a machine behind a corporate proxy works
/// without configuration. No overall timeout on the client: each call sets its own via a token,
/// because a catalog fetch and a 300 MB installer download need very different limits.
/// </remarks>
public static class UpdatrHttp
{
    public const string Repository = "matthewjenner/Updatr";

    public const string RawCatalogBase = $"https://raw.githubusercontent.com/{Repository}/main/catalog/";

    public const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        SocketsHttpHandler handler = new()
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };

        HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ProductInfo.Name, ProductInfo.Version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(+https://github.com/{Repository})"));
        return client;
    }

    /// <summary>A token that cancels after <paramref name="timeout"/> or when <paramref name="cancellationToken"/> does.</summary>
    public static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken cancellationToken)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }
}
