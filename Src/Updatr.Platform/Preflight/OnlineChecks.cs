using System.Net;
using System.Text.Json;
using Updatr.Core;
using Updatr.Core.Catalog;
using Updatr.Core.Diagnostics;
using Updatr.Core.Preflight;
using Updatr.Platform.Net;

namespace Updatr.Platform.Preflight;

/// <summary>
/// Loads the catalog: GitHub when online, else the copy saved on the drive, else the built-in one.
/// The loaded catalog goes into <see cref="PreflightContext.Catalog"/> for the app to use.
/// </summary>
public sealed class CatalogCheck(ICatalogOrigin remote, ICatalogCache cache) : IPreflightCheck
{
    public string Id => "catalog";

    public string Title => "App catalog";

    public IReadOnlyList<string> DependsOn => ["network"];

    public async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        // Offline: skip the remote rather than wait for its timeout.
        List<ICatalogOrigin> origins = context.IsOnline == true ? [remote, cache] : [cache];
        origins.Add(new EmbeddedCatalogOrigin());

        // Developer override: a local catalog folder ahead of everything (UPDATR_CATALOG_DIR).
        if (Catalog.FolderCatalogOrigin.FromEnvironment() is { } folder)
        {
            DiagLog.CatalogOverride(folder.Folder);
            origins.Insert(0, folder);
        }

        CatalogLoadResult result;

        try
        {
            result = await new CatalogLoader(origins, cache).LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CatalogUnavailableException ex)
        {
            return PreflightResult.Fail("No usable catalog", string.Join("\n", ex.Diagnostics));
        }

        context.Catalog = result;

        foreach (string diagnostic in result.Diagnostics)
            DiagLog.CatalogOriginSkipped(diagnostic);

        DiagLog.CatalogLoaded(result.Origin.ToString(), result.Catalog.Apps.Count, result.Catalog.Templates.Count, result.Catalog.Holds.Count, result.Timestamp);

        string counts = $"{result.Catalog.Apps.Count} apps, {result.Catalog.Templates.Count} templates";
        string? detail = result.Diagnostics.Count == 0 ? null : string.Join("\n", result.Diagnostics);

        if (result.NewerSchemaAvailable)
            detail = (detail is null ? "" : detail + "\n") + "A newer catalog exists that this version cannot read - update Updatr.";

        if (Catalog.FolderCatalogOrigin.FromEnvironment() is { } overridden && result.Origin == CatalogOriginKind.Embedded)
            return PreflightResult.Warn($"Local catalog folder ({counts})", $"UPDATR_CATALOG_DIR is set to {overridden.Folder} - for testing catalog edits. Unset it to use GitHub's catalog.");

        return result.Origin switch
        {
            CatalogOriginKind.Remote => PreflightResult.Pass($"Latest from GitHub ({counts})", detail),
            CatalogOriginKind.Cache => PreflightResult.Warn($"Saved copy from {result.Timestamp:yyyy-MM-dd} ({counts})", detail),
            _ => PreflightResult.Warn($"Built-in copy ({counts})", detail),
        };
    }
}

/// <summary>Whether a newer Updatr is published on GitHub. Informational.</summary>
public sealed class SelfUpdateCheck(HttpClient http) : IPreflightCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public string Id => "self-update";

    public string Title => "Updatr version";

    public IReadOnlyList<string> DependsOn => ["network"];

    public async Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        string current = ProductInfo.Version;

        if (context.IsOnline != true)
            return PreflightResult.Info($"v{current} (not checked - offline)");

        try
        {
            using CancellationTokenSource deadline = UpdatrHttp.Deadline(Timeout, cancellationToken);
            using HttpResponseMessage response = await http.GetAsync(UpdatrHttp.LatestReleaseApi, deadline.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return PreflightResult.Info($"v{current} (no releases published yet)");

            response.EnsureSuccessStatusCode();

            using JsonDocument release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            string tag = release.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            string latest = tag.TrimStart('v', 'V');

            if (Version.TryParse(latest, out Version? latestVersion)
                && Version.TryParse(current, out Version? currentVersion)
                && latestVersion > currentVersion)
            {
                context.NewerVersion = latest;
                return PreflightResult.Info($"v{current} - v{latest} is available");
            }

            return PreflightResult.Pass($"v{current} (latest)");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PreflightResult.Info($"v{current} (could not check: {ex.Message})");
        }
    }
}
