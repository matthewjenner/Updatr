using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Updatr.Core.Sources;

/// <summary>A package an ecosystem tool reports as installed.</summary>
public sealed record ToolPackage(string Name, string Version);

/// <summary>
/// Pure parsers for what the ecosystem CLIs and registries return. Kept here (not beside the
/// process calls) so every format is unit-tested against real captured output.
/// </summary>
public static partial class EcosystemParsers
{
    // ---- npm ------------------------------------------------------------------------------

    /// <summary><c>npm ls -g --depth=0 --json</c>: <c>{ "dependencies": { name: { "version": "x" } } }</c>.</summary>
    public static IReadOnlyList<ToolPackage> NpmList(string json)
    {
        using JsonDocument doc = Parse(json);

        if (!doc.RootElement.TryGetProperty("dependencies", out JsonElement deps))
            return [];

        return [.. deps.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("version", out _))
            .Select(p => new ToolPackage(p.Name, p.Value.GetProperty("version").GetString() ?? string.Empty))];
    }

    /// <summary>
    /// <c>npm outdated -g --json</c>: <c>{ name: { "current": "x", "latest": "y" } }</c>. Empty
    /// output or <c>{}</c> means nothing is outdated. (npm exits 1 when something is - not an error.)
    /// </summary>
    public static IReadOnlyDictionary<string, string> NpmOutdated(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();

        using JsonDocument doc = Parse(json);

        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.TryGetProperty("latest", out JsonElement l) && l.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("latest").GetString()!, StringComparer.Ordinal);
    }

    // ---- pipx / PyPI ----------------------------------------------------------------------

    /// <summary><c>pipx list --json</c>: <c>{ "venvs": { name: { "metadata": { "main_package": { "package", "package_version" } } } } }</c>.</summary>
    public static IReadOnlyList<ToolPackage> PipxList(string json)
    {
        using JsonDocument doc = Parse(json);

        if (!doc.RootElement.TryGetProperty("venvs", out JsonElement venvs))
            return [];

        List<ToolPackage> result = [];

        foreach (JsonProperty venv in venvs.EnumerateObject())
        {
            if (venv.Value.TryGetProperty("metadata", out JsonElement meta)
                && meta.TryGetProperty("main_package", out JsonElement main)
                && main.TryGetProperty("package_version", out JsonElement version))
            {
                string name = main.TryGetProperty("package", out JsonElement pkg) ? pkg.GetString() ?? venv.Name : venv.Name;
                result.Add(new ToolPackage(name, version.GetString() ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>PyPI JSON API (<c>/pypi/{name}/json</c>): <c>info.version</c> is the latest release.</summary>
    public static string? PyPiLatest(string json)
    {
        using JsonDocument doc = Parse(json);
        return doc.RootElement.TryGetProperty("info", out JsonElement info) && info.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
    }

    // ---- dotnet tools / NuGet --------------------------------------------------------------

    /// <summary>
    /// <c>dotnet tool list -g</c>: a fixed table, "Package Id  Version  Commands", a dashes rule,
    /// then rows. Columns are whitespace-separated (ids and versions never contain spaces).
    /// </summary>
    public static IReadOnlyList<ToolPackage> DotnetToolList(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int rule = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));

        if (rule < 0)
            return [];

        return [.. lines.Skip(rule + 1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new ToolPackage(parts[0], parts[1]))];
    }

    /// <summary>NuGet flat container (<c>/v3-flatcontainer/{id}/index.json</c>): the newest stable version.</summary>
    public static string? NuGetLatestStable(string json)
    {
        using JsonDocument doc = Parse(json);

        if (!doc.RootElement.TryGetProperty("versions", out JsonElement versions))
            return null;

        return versions.EnumerateArray()
            .Select(v => v.GetString())
            .OfType<string>()
            .Where(VersionComparer.IsStable)
            .OrderBy(v => v, VersionComparer.Instance)
            .LastOrDefault();
    }

    // ---- PowerShell Gallery ----------------------------------------------------------------

    /// <summary>PowerShell Gallery OData feed (<c>FindPackagesById()?id='x'&amp;$filter=IsLatestVersion</c>): the version.</summary>
    public static string? PsGalleryLatest(string xml)
    {
        XDocument doc = XDocument.Parse(xml);
        XNamespace d = "http://schemas.microsoft.com/ado/2007/08/dataservices";

        return doc.Descendants(d + "Version").Select(e => e.Value).OrderBy(v => v, VersionComparer.Instance).LastOrDefault();
    }

    /// <summary>
    /// <c>PSGetModuleInfo.xml</c> (CLIXML written by PowerShellGet / PSResourceGet beside an
    /// installed module): the repository it came from. Only <c>PSGallery</c> modules are managed.
    /// </summary>
    public static string? PsGetRepository(string clixml)
    {
        Match m = RepositoryPattern().Match(clixml);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("<S N=\"Repository\">([^<]*)</S>", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    // ---- VS Code ---------------------------------------------------------------------------

    /// <summary><c>code --list-extensions --show-versions</c>: one <c>publisher.name@version</c> per line.</summary>
    public static IReadOnlyList<ToolPackage> VsCodeList(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('@'))
            .Where(p => p.Length == 2 && p[0].Contains('.', StringComparison.Ordinal))
            .Select(p => new ToolPackage(p[0], p[1]))];

    /// <summary>What a Marketplace query found.</summary>
    /// <param name="Latest">Extension id to its newest <b>stable</b> version for the platform.</param>
    /// <param name="OnlyPreRelease">
    /// Ids whose returned versions were all pre-releases. A "latest only" query returns just the
    /// newest build, which may be a pre-release (seen live: GitLens 2026.10.x pre-release while the
    /// newest stable was 19.4.0), so these need a second query for their full history.
    /// </param>
    public sealed record MarketplaceResult(IReadOnlyDictionary<string, string> Latest, IReadOnlyList<string> OnlyPreRelease);

    /// <summary>
    /// VS Code Marketplace <c>extensionquery</c> response: each extension's newest stable version for
    /// <paramref name="platform"/> (e.g. <c>win32-x64</c>).
    /// </summary>
    /// <remarks>
    /// Two traps, both seen live: extensions with native code publish one latest version per
    /// platform plus sometimes a much older universal one (Claude Code: universal 2.1.89, win32-x64
    /// 2.1.310), so the newest of this-platform-or-universal entries is used, never the first; and
    /// pre-releases (property <c>Microsoft.VisualStudio.Code.PreRelease</c>) are never offered as
    /// updates.
    /// </remarks>
    public static MarketplaceResult VsCodeMarketplace(string json, string platform)
    {
        using JsonDocument doc = Parse(json);
        Dictionary<string, string> latest = new(StringComparer.OrdinalIgnoreCase);
        List<string> onlyPreRelease = [];

        foreach (JsonElement result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            foreach (JsonElement ext in result.GetProperty("extensions").EnumerateArray())
            {
                string publisher = ext.GetProperty("publisher").GetProperty("publisherName").GetString() ?? string.Empty;
                string id = $"{publisher}.{ext.GetProperty("extensionName").GetString()}";

                if (!ext.TryGetProperty("versions", out JsonElement versions))
                    continue;

                List<JsonElement> forPlatform = [.. versions.EnumerateArray().Where(v => !v.TryGetProperty("targetPlatform", out JsonElement tp)
                    || tp.ValueKind == JsonValueKind.Null
                    || string.Equals(tp.GetString(), platform, StringComparison.OrdinalIgnoreCase))];

                string? newestStable = forPlatform
                    .Where(v => !IsPreRelease(v))
                    .Select(v => v.GetProperty("version").GetString())
                    .OfType<string>()
                    .OrderBy(v => v, VersionComparer.Instance)
                    .LastOrDefault();

                if (newestStable is not null)
                    latest[id] = newestStable;
                else if (forPlatform.Count > 0)
                    onlyPreRelease.Add(id);
            }
        }

        return new MarketplaceResult(latest, onlyPreRelease);

        static bool IsPreRelease(JsonElement version)
            => version.TryGetProperty("properties", out JsonElement props)
                && props.EnumerateArray().Any(p => p.GetProperty("key").GetString() == "Microsoft.VisualStudio.Code.PreRelease"
                    && string.Equals(p.GetProperty("value").GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Argument safety --------------------------------------------------------------------

    /// <summary>
    /// npm, VS Code, and pipx are batch files (<c>.cmd</c>) on Windows, and cmd.exe re-parses
    /// arguments - a name containing <c>&amp;</c>, <c>|</c>, <c>%</c> or quotes could run something
    /// else. Every package name passed to an ecosystem CLI must match this first.
    /// </summary>
    public static bool IsSafePackageName(string name) => SafeName().IsMatch(name);

    [GeneratedRegex(@"^(@[A-Za-z0-9][A-Za-z0-9._~-]*/)?[A-Za-z0-9][A-Za-z0-9._~-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    /// <summary>Version strings passed to a CLI: digits, letters, dots, dashes, plus.</summary>
    public static bool IsSafeVersion(string version) => SafeVersion().IsMatch(version);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersion();

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
}
