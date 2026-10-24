using System.Diagnostics;
using System.Net;
using Updatr.Core.Preflight;
using Updatr.Platform.Net;

namespace Updatr.Platform.Preflight;

/// <summary>
/// Can Updatr reach what it downloads from? Probes the hosts it actually uses, in parallel, with
/// a short deadline each. All reachable is Pass, some is Warn, none is Fail (offline mode).
/// </summary>
public sealed class NetworkCheck(HttpClient http) : IndependentCheck
{
    private static readonly TimeSpan PerHost = TimeSpan.FromSeconds(6);

    /// <summary>Host, what it is for, and a URL that answers quickly (any HTTP status counts as reachable).</summary>
    private static readonly (string Host, string Purpose, string Url)[] Endpoints =
    [
        ("api.github.com", "self-update", "https://api.github.com/"),
        ("raw.githubusercontent.com", "catalog", UpdatrHttp.RawCatalogBase + "apps.json"),
        ("cdn.winget.microsoft.com", "winget index", "https://cdn.winget.microsoft.com/cache/source.msix"),
    ];

    public override string Id => "network";

    public override string Title => "Internet connection";

    public override async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        string[] probes = await Task.WhenAll(Endpoints.Select(e => ProbeAsync(e.Host, e.Purpose, e.Url, cancellationToken))).ConfigureAwait(false);
        int reachable = probes.Count(p => p.StartsWith("ok", StringComparison.Ordinal));
        context.IsOnline = reachable > 0;

        string proxy = DescribeProxy();
        string detail = string.Join("\n", probes.Select((p, i) => $"{Endpoints[i].Host} ({Endpoints[i].Purpose}): {p}")) + $"\nProxy: {proxy}";

        return reachable == Endpoints.Length ? PreflightResult.Pass("Online", detail)
            : reachable > 0 ? PreflightResult.Warn($"Partly reachable ({reachable} of {Endpoints.Length} services)", detail)
            : PreflightResult.Fail("Offline - nothing can be downloaded", detail + "\nUpdates and installs need the internet; defluff and the saved catalog still work offline.");
    }

    private async Task<string> ProbeAsync(string host, string purpose, string url, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            // DNS first, separately, so "no DNS" and "blocked" read differently in the detail.
            using (CancellationTokenSource dns = UpdatrHttp.Deadline(PerHost, cancellationToken))
                await Dns.GetHostAddressesAsync(host, dns.Token).ConfigureAwait(false);

            using CancellationTokenSource deadline = UpdatrHttp.Deadline(PerHost, cancellationToken);
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            return $"ok ({(int)response.StatusCode}, {timer.ElapsedMilliseconds} ms)";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"timed out after {PerHost.TotalSeconds:0} s";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            return $"failed ({ex.Message})";
        }
    }

    private static string DescribeProxy()
    {
        Uri probe = new("https://api.github.com/");
        Uri? proxy = HttpClient.DefaultProxy.GetProxy(probe);
        return proxy is null || proxy == probe ? "none (direct)" : proxy.ToString();
    }
}
