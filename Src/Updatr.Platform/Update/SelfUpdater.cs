using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Updatr.Core;
using Updatr.Core.Diagnostics;
using Updatr.Core.Operations;
using Updatr.Platform.Data;
using Updatr.Platform.Net;

namespace Updatr.Platform.Update;

/// <summary>What a self-update did.</summary>
/// <param name="Updated">The files beside the exe were replaced; the next start runs the new version.</param>
/// <param name="Version">The version now on the drive (the new one when updated).</param>
/// <param name="Summary">One line for the user.</param>
/// <param name="Failed">Something went wrong (as opposed to "already the latest" or "not confirmed").</param>
public sealed record SelfUpdateResult(bool Updated, string Version, string Summary, bool Failed = false);

/// <summary>
/// Updates Updatr on the drive from the latest GitHub release: download the zip for this
/// architecture to the host's temp folder, check it against the release's SHA256SUMS.txt, then
/// swap the files beside the exe.
/// </summary>
/// <remarks>
/// A running exe cannot be overwritten but can be renamed, so each current file is renamed to
/// <c>*.old</c> first and the new one copied in; any failure puts the old files back.
/// <see cref="CleanUpOld"/> removes the <c>*.old</c> files on a later start. Development builds
/// (version 0.0.0, or not a single-file publish) refuse, so a dev run never rewrites <c>bin\</c>.
/// </remarks>
[Operation("self-update")]
public sealed class SelfUpdater(HttpClient http) : IOperation<bool, SelfUpdateResult>
{
    /// <summary>What a release zip holds, and so what gets replaced.</summary>
    public static readonly IReadOnlyList<string> ReleaseFiles = ["Updatr.exe", "updatr-cli.exe", "Microsoft.Management.Deployment.winmd"];

    private const string SumsFile = "SHA256SUMS.txt";
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Request: true to reinstall the latest even when already on it.</summary>
    public async Task<SelfUpdateResult> RunAsync(bool force, IOperationSink sink, CancellationToken cancellationToken)
    {
        string current = ProductInfo.Version;
        string folder = AppLocations.ExeDirectory;

        if (DevelopmentBuildReason(current, folder) is { } dev)
            return new SelfUpdateResult(false, current, dev);

        sink.Report(new StepStarted("Checking GitHub for the latest release"));
        Release release;

        try
        {
            release = await LatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new SelfUpdateResult(false, current, $"Could not check GitHub for a new version: {ex.Message}", Failed: true);
        }

        if (!force && Version.TryParse(release.Version, out Version? latest) && Version.TryParse(current, out Version? mine) && latest <= mine)
            return new SelfUpdateResult(false, current, $"Updatr v{current} is the latest.");

        string asset = AssetName(release.Version, RuntimeInformation.ProcessArchitecture);

        if (!release.Assets.TryGetValue(asset, out string? zipUrl) || !release.Assets.TryGetValue(SumsFile, out string? sumsUrl))
            return new SelfUpdateResult(false, current, $"Release v{release.Version} has no {asset} (or no {SumsFile}).", Failed: true);

        if (!await sink.ConfirmAsync(new Confirmation($"Update Updatr from v{current} to v{release.Version}?",
            $"Downloads {asset}, checks it, and replaces Updatr.exe, updatr-cli.exe, and the WinGet metadata in\n  {folder}\nThe new version runs from the next start."), cancellationToken).ConfigureAwait(false))
            return new SelfUpdateResult(false, current, "Not confirmed.");

        string temp = Path.Combine(Path.GetTempPath(), "Updatr", $"self-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            sink.Report(new StepStarted($"Downloading v{release.Version}"));
            string zip = Path.Combine(temp, asset);
            string sums = Path.Combine(temp, SumsFile);
            Downloader downloader = new(http);
            await downloader.DownloadAsync([sumsUrl], sums, null, null, cancellationToken).ConfigureAwait(false);
            await downloader.DownloadAsync([zipUrl], zip, sink, "self-update", cancellationToken).ConfigureAwait(false);

            sink.Report(new StepStarted("Checking the download"));
            string? expected = ParseSums(await File.ReadAllTextAsync(sums, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(asset);

            if (expected is null)
                return new SelfUpdateResult(false, current, $"{SumsFile} does not list {asset}; not updating.", Failed: true);

            if (!string.Equals(await HashAsync(zip, cancellationToken).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
                return new SelfUpdateResult(false, current, "The download does not match its published checksum; not updating.", Failed: true);

            string staging = Path.Combine(temp, "staging");
            ZipFile.ExtractToDirectory(zip, staging);
            string payload = FindPayload(staging) ?? throw new InvalidDataException($"{asset} does not contain {string.Join(", ", ReleaseFiles)}.");

            sink.Report(new StepStarted("Replacing the files on the drive"));
            Swap(payload, folder);
            DiagLog.SelfUpdated(current, release.Version);
            return new SelfUpdateResult(true, release.Version, $"Updated to v{release.Version}. Restart Updatr to use it.");
        }
        catch (Exception ex) when (ex is DownloadFailedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SelfUpdateResult(false, current, $"Update failed, nothing was changed: {ex.Message}", Failed: true);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best effort.
            }
        }
    }

    /// <summary>Why this build must not update itself, or null.</summary>
    public static string? DevelopmentBuildReason(string version, string folder)
        => version == "0.0.0" ? "This is a development build (v0.0.0); self-update is for released builds."
            : File.Exists(Path.Combine(folder, "Updatr.Core.dll")) ? "This is a development build (not a single-file publish); self-update is for released builds."
            : null;

    /// <summary>The zip a release ships for this architecture, as Scripts/publish.sh names it.</summary>
    public static string AssetName(string version, Architecture architecture)
        => $"Updatr-{version}-{(architecture == Architecture.Arm64 ? "win-arm64" : "win-x64")}.zip";

    /// <summary>Reads "&lt;sha256&gt;  &lt;file&gt;" lines (sha256sum's format).</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        Dictionary<string, string> sums = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 2 && parts[0].Length == 64)
                sums[parts[1].TrimStart('*')] = parts[0];
        }

        return sums;
    }

    /// <summary>
    /// Replaces the release files in <paramref name="target"/> with those in <paramref name="source"/>.
    /// All or nothing: if any step fails, the files already moved aside are put back.
    /// </summary>
    public static void Swap(string source, string target)
    {
        List<(string Live, string Old)> movedAside = [];
        List<string> copied = [];

        try
        {
            foreach (string file in ReleaseFiles)
            {
                string live = Path.Combine(target, file);

                if (File.Exists(live))
                {
                    string old = FreeOldName(live);
                    File.Move(live, old); // Allowed while it runs; overwriting is not.
                    movedAside.Add((live, old));
                }

                File.Copy(Path.Combine(source, file), live);
                copied.Add(live);
            }
        }
        catch
        {
            foreach (string live in copied)
                File.Delete(live);

            foreach ((string live, string old) in movedAside)
                File.Move(old, live);

            throw;
        }
    }

    /// <summary>Removes <c>*.old</c> files a previous update left beside the exe. Best effort: one may still be running.</summary>
    public static void CleanUpOld(string folder)
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(folder, "*.old*"))
            {
                if (!ReleaseFiles.Any(f => Path.GetFileName(old).StartsWith(f + ".old", StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still running (the other front end) - next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or missing folder: nothing to clean.
        }
    }

    /// <summary><c>x.old</c>, or <c>x.old2</c>... when an older one is still locked by a running copy.</summary>
    private static string FreeOldName(string live)
    {
        for (int n = 1; ; n++)
        {
            string candidate = live + (n == 1 ? ".old" : $".old{n}");

            if (!File.Exists(candidate))
                return candidate;

            try
            {
                File.Delete(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use; try the next name.
            }
        }
    }

    /// <summary>The folder inside the extracted zip that holds the release files (the zip has a top folder).</summary>
    private static string? FindPayload(string staging)
        => new[] { staging }.Concat(Directory.EnumerateDirectories(staging))
            .FirstOrDefault(d => ReleaseFiles.All(f => File.Exists(Path.Combine(d, f))));

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Release> LatestAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = UpdatrHttp.Deadline(LookupTimeout, cancellationToken);
        using HttpResponseMessage response = await http.GetAsync(UpdatrHttp.LatestReleaseApi, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
        string version = (json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty).TrimStart('v', 'V');
        Dictionary<string, string> assets = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() is { } name && asset.GetProperty("browser_download_url").GetString() is { } url)
                assets[name] = url;
        }

        return new Release(version, assets);
    }

    private sealed record Release(string Version, IReadOnlyDictionary<string, string> Assets);
}
