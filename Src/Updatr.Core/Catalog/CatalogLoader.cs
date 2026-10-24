using System.Text.Json;

namespace Updatr.Core.Catalog;

/// <summary>The catalog Updatr will run with, and how it got it.</summary>
/// <param name="Catalog">The validated catalog.</param>
/// <param name="Origin">Which origin supplied it.</param>
/// <param name="Timestamp">When that set was fetched, if known.</param>
/// <param name="Diagnostics">One line per origin that was tried and passed over, and why.</param>
/// <param name="NewerSchemaAvailable">
/// True when some origin had a catalog newer than this build understands - a hint to self-update.
/// </param>
public sealed record CatalogLoadResult(
    AppCatalog Catalog,
    CatalogOriginKind Origin,
    DateTimeOffset? Timestamp,
    IReadOnlyList<string> Diagnostics,
    bool NewerSchemaAvailable);

/// <summary>The outcome of parsing and validating one catalog set.</summary>
public sealed record CatalogParseResult(AppCatalog? Catalog, IReadOnlyList<string> Issues, bool NewerSchema);

/// <summary>Thrown when no origin - not even the embedded copy - yields a usable catalog.</summary>
public sealed class CatalogUnavailableException(IReadOnlyList<string> diagnostics)
    : Exception("No usable catalog: " + string.Join("; ", diagnostics))
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Loads the catalog from the first origin that yields a complete, valid set: remote, then cache,
/// then embedded. A set is used whole or not at all, so templates never point at apps from a
/// different version of the catalog.
/// </summary>
public sealed class CatalogLoader(IReadOnlyList<ICatalogOrigin> origins, ICatalogCache? cache = null)
{
    public async Task<CatalogLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        List<string> diagnostics = [];
        bool newerSchema = false;

        foreach (ICatalogOrigin origin in origins)
        {
            CatalogFileSet? set;

            try
            {
                set = await origin.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{origin.Kind}: unavailable ({ex.GetType().Name}: {ex.Message})");
                continue;
            }

            if (set is null)
            {
                diagnostics.Add($"{origin.Kind}: nothing stored");
                continue;
            }

            CatalogParseResult parsed = Parse(set, CatalogJson.Options);
            newerSchema |= parsed.NewerSchema;

            if (parsed.Catalog is null)
            {
                diagnostics.Add($"{origin.Kind}: rejected ({string.Join("; ", parsed.Issues)})");
                continue;
            }

            if (origin.Kind == CatalogOriginKind.Remote && cache is not null)
                await TryWriteCacheAsync(set, diagnostics, cancellationToken).ConfigureAwait(false);

            return new CatalogLoadResult(parsed.Catalog, origin.Kind, set.Timestamp, diagnostics, newerSchema);
        }

        throw new CatalogUnavailableException(diagnostics);
    }

    /// <summary>Parses and validates one set. Never throws for bad content; issues are returned.</summary>
    public static CatalogParseResult Parse(CatalogFileSet set, JsonSerializerOptions options)
    {
        List<string> issues = [];
        bool newerSchema = false;

        T? Read<T>(string file) where T : CatalogFileBase
        {
            if (!set.Files.TryGetValue(file, out string? json))
            {
                issues.Add($"{file}: missing");
                return null;
            }

            T? value;

            try
            {
                value = JsonSerializer.Deserialize<T>(json, options);
            }
            catch (JsonException ex)
            {
                issues.Add($"{file}: invalid JSON ({ex.Message})");
                return null;
            }

            if (value is null)
            {
                issues.Add($"{file}: empty");
                return null;
            }

            if (value.SchemaVersion > CatalogFiles.SchemaVersion)
            {
                newerSchema = true;
                issues.Add($"{file}: schemaVersion {value.SchemaVersion} is newer than this build understands ({CatalogFiles.SchemaVersion})");
                return null;
            }

            if (value.SchemaVersion < 1)
            {
                issues.Add($"{file}: schemaVersion must be 1 or higher");
                return null;
            }

            return value;
        }

        AppsFile? apps = Read<AppsFile>(CatalogFiles.Apps);
        TemplatesFile? templates = Read<TemplatesFile>(CatalogFiles.Templates);
        HoldsFile? holds = Read<HoldsFile>(CatalogFiles.Holds);

        if (apps is null || templates is null || holds is null)
            return new CatalogParseResult(null, issues, newerSchema);

        issues.AddRange(CatalogValidator.Validate(apps, templates, holds));

        if (issues.Count > 0)
            return new CatalogParseResult(null, issues, newerSchema);

        return new CatalogParseResult(new AppCatalog(apps.Apps, templates.Templates, holds.Holds), issues, newerSchema);
    }

    private async Task TryWriteCacheAsync(CatalogFileSet set, List<string> diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            await cache!.WriteAsync(set, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A read-only or yanked drive must not stop Updatr from using the catalog it just got.
            diagnostics.Add($"Cache: could not save ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
