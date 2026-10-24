using System.Net;
using Updatr.Core.Operations;

namespace Updatr.Platform.Net;

/// <summary>
/// Downloads to a local file, robustly: a slow link is fine, a stalled one is not.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Stall detection, not a total timeout.</b> An attempt fails when no bytes arrive for
/// <see cref="StallTimeout"/>; a 2 GB SDK on a slow line still finishes.</item>
/// <item><b>Retries with backoff</b> per URL, then the next URL (mirrors), in order.</item>
/// <item><b>Never a partial file.</b> Bytes go to <c>.part</c> and are renamed when complete.</item>
/// <item>Progress (bytes, total, throughput) goes to the sink so "slow" is visible.</item>
/// </list>
/// </remarks>
public sealed class Downloader(HttpClient http)
{
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int AttemptsPerUrl { get; init; } = 3;

    /// <summary>Delays between attempts on one URL.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>Downloads the first URL that works into <paramref name="path"/>. Throws when every URL fails.</summary>
    public async Task DownloadAsync(IReadOnlyList<string> urls, string path, IOperationSink? sink, string? itemId, CancellationToken cancellationToken)
    {
        List<string> failures = [];

        foreach (string url in urls)
        {
            for (int attempt = 1; attempt <= AttemptsPerUrl; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(url, path, sink, itemId, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException)
                {
                    string reason = ex is OperationCanceledException ? $"stalled (no data for {StallTimeout.TotalSeconds:0} s)" : ex.Message;
                    failures.Add($"{url} attempt {attempt}: {reason}");

                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone })
                        break; // Will not change on retry; try the next URL.

                    if (attempt < AttemptsPerUrl)
                    {
                        sink?.Report(new ProgressReported(itemId, null, Message: $"Download problem ({reason}); retrying"));
                        await Task.Delay(Backoff[Math.Min(attempt - 1, Backoff.Count - 1)], cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        throw new DownloadFailedException(failures);
    }

    private async Task DownloadOnceAsync(string url, string path, IOperationSink? sink, string? itemId, CancellationToken cancellationToken)
    {
        string part = path + ".part";
        using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using (Stream input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
        await using (FileStream output = new(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long done = 0;
            long lastReport = 0;
            DateTime started = DateTime.UtcNow;
            int read;

            while ((read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                // Data arrived: the stall clock starts over.
                stall.CancelAfter(StallTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                done += read;

                if (done - lastReport >= 512 * 1024 || done == total)
                {
                    lastReport = done;
                    double seconds = Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds);
                    sink?.Report(new ProgressReported(itemId, total is > 0 ? (double)done / total.Value : null, done, total,
                        $"Downloading ({done / seconds / 1048576.0:0.0} MB/s)"));
                }
            }

            if (total is { } expected && done != expected)
                throw new IOException($"download ended early ({done} of {expected} bytes)");
        }

        File.Move(part, path, overwrite: true);
    }
}

/// <summary>Every URL and attempt failed. The message lists each failure.</summary>
public sealed class DownloadFailedException(IReadOnlyList<string> failures)
    : Exception("Download failed: " + string.Join("; ", failures))
{
    public IReadOnlyList<string> Failures { get; } = failures;
}
