using System.Text.RegularExpressions;

namespace Updatr.Core.Catalog;

/// <summary>
/// Semantic checks the JSON shape cannot express: unique ids, references that resolve, sources
/// that are complete. Any issue makes the whole catalog set unusable, so the loader falls back to
/// the next origin rather than running with half a catalog.
/// </summary>
public static partial class CatalogValidator
{
    /// <summary>Lowercase letters and digits, words joined by single hyphens.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Validates a parsed catalog set. Returns every issue found, empty when valid.</summary>
    public static IReadOnlyList<string> Validate(AppsFile apps, TemplatesFile templates, HoldsFile holds)
    {
        List<string> issues = [];
        HashSet<string> appIds = new(StringComparer.Ordinal);

        foreach (CatalogApp app in apps.Apps)
        {
            string where = $"{CatalogFiles.Apps}: app '{app.Id}'";

            if (!IdPattern().IsMatch(app.Id))
                issues.Add($"{where}: id must be lowercase letters/digits joined by single hyphens");
            if (!appIds.Add(app.Id))
                issues.Add($"{where}: duplicate id");
            if (string.IsNullOrWhiteSpace(app.Name))
                issues.Add($"{where}: name is empty");
            if (string.IsNullOrWhiteSpace(app.Category))
                issues.Add($"{where}: category is empty");
            if (app.Homepage is not null && !IsHttps(app.Homepage))
                issues.Add($"{where}: homepage must be an https URL");
            if (app.Sources.Count == 0)
                issues.Add($"{where}: needs at least one source");

            foreach (PackageSourceRef source in app.Sources)
                ValidateSource(source, where, issues);
        }

        foreach (CatalogApp app in apps.Apps)
        {
            foreach (string required in app.Requires)
            {
                if (!appIds.Contains(required))
                    issues.Add($"{CatalogFiles.Apps}: app '{app.Id}': requires unknown app '{required}'");
            }
        }

        HashSet<string> templateIds = new(StringComparer.Ordinal);

        foreach (CatalogTemplate template in templates.Templates)
        {
            string where = $"{CatalogFiles.Templates}: template '{template.Id}'";

            if (!IdPattern().IsMatch(template.Id))
                issues.Add($"{where}: id must be lowercase letters/digits joined by single hyphens");
            if (!templateIds.Add(template.Id))
                issues.Add($"{where}: duplicate id");
            if (string.IsNullOrWhiteSpace(template.Name))
                issues.Add($"{where}: name is empty");

            foreach (string item in template.Items)
            {
                if (!appIds.Contains(item))
                    issues.Add($"{where}: unknown app '{item}'");
            }
        }

        foreach (HoldRule hold in holds.Holds)
        {
            string where = $"{CatalogFiles.Holds}: pattern '{hold.Pattern}'";

            if (string.IsNullOrWhiteSpace(hold.Pattern))
                issues.Add($"{CatalogFiles.Holds}: a rule has an empty pattern");
            if (string.IsNullOrWhiteSpace(hold.Source))
                issues.Add($"{where}: source is empty");
            if (string.IsNullOrWhiteSpace(hold.Reason))
                issues.Add($"{where}: reason is empty");
        }

        return issues;
    }

    /// <summary>Service names: what the SCM accepts and nothing a shell could misread.</summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,80}$", RegexOptions.CultureInvariant)]
    public static partial Regex ServiceNamePattern();

    /// <summary>Validates the debloat file. Returns every issue found, empty when valid.</summary>
    public static IReadOnlyList<string> Validate(DebloatFile debloat)
    {
        List<string> issues = [];
        HashSet<string> ids = new(StringComparer.Ordinal);

        foreach (DebloatItem item in debloat.Items)
        {
            string where = $"{CatalogFiles.Debloat}: item '{item.Id}'";

            if (!IdPattern().IsMatch(item.Id))
                issues.Add($"{where}: id must be lowercase letters/digits joined by single hyphens");
            if (!ids.Add(item.Id))
                issues.Add($"{where}: duplicate id");
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Category) || string.IsNullOrWhiteSpace(item.Description))
                issues.Add($"{where}: title, category, and description are required");
            if (item.Actions.Count == 0)
                issues.Add($"{where}: needs at least one action");

            foreach (DebloatAction action in item.Actions)
            {
                if (ValidateAction(action) is { } problem)
                    issues.Add($"{where}: {problem}");
            }
        }

        if (debloat.Keep.Any(string.IsNullOrWhiteSpace))
            issues.Add($"{CatalogFiles.Debloat}: keep has an empty pattern");

        return issues;
    }

    /// <summary>Why a debloat action is malformed, or null. Also used by the elevated broker on what it is sent.</summary>
    public static string? ValidateAction(DebloatAction action) => action switch
    {
        RegistryAction r when string.IsNullOrWhiteSpace(r.Key) || string.IsNullOrWhiteSpace(r.Name)
            => "registry action needs a key and a value name",
        RegistryAction r when r.Key.StartsWith('\\') || r.Key.EndsWith('\\') || r.Key.Contains("\\\\", StringComparison.Ordinal)
            => $"registry key '{r.Key}' must not start or end with \\ or contain empty parts",
        RegistryAction r when r.Hive == RegistryHive.LocalMachine && !r.Key.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)
            => $"machine registry key '{r.Key}' must be under SOFTWARE\\",
        RegistryAction r when (r.Dword is not null ? 1 : 0) + (r.String is not null ? 1 : 0) + (r.Delete ? 1 : 0) != 1
            => $"registry value '{r.Name}' needs exactly one of dword, string, or delete",
        AppxAction a when string.IsNullOrWhiteSpace(a.Name) || a.Name.Trim('*', '?', '.').Length == 0
            => "appx action needs a package name (not just wildcards)",
        ServiceAction s when !ServiceNamePattern().IsMatch(s.Name)
            => $"service name '{s.Name}' is not a valid service name",
        ScheduledTaskAction t when !t.Path.StartsWith('\\') || t.Path.EndsWith('\\')
            => $"task path '{t.Path}' must be a full path starting with \\",
        _ => null,
    };

    private static void ValidateSource(PackageSourceRef source, string where, List<string> issues)
    {
        switch (source)
        {
            case WingetSourceRef w when string.IsNullOrWhiteSpace(w.Id):
                issues.Add($"{where}: winget source has an empty id");
                break;
            case NpmSourceRef n when string.IsNullOrWhiteSpace(n.Package):
            case PipxSourceRef p when string.IsNullOrWhiteSpace(p.Package):
            case DotnetToolSourceRef d when string.IsNullOrWhiteSpace(d.Package):
                issues.Add($"{where}: {source.SourceId} source has an empty package");
                break;
            case PsGallerySourceRef g when string.IsNullOrWhiteSpace(g.Module):
                issues.Add($"{where}: psGallery source has an empty module");
                break;
            case VsCodeExtensionSourceRef v when !v.Extension.Contains('.', StringComparison.Ordinal):
                issues.Add($"{where}: vscode extension id must be publisher.name");
                break;
            case DirectUrlSourceRef u:
                ValidateDirectUrl(u, where, issues);
                break;
        }
    }

    private static void ValidateDirectUrl(DirectUrlSourceRef source, string where, List<string> issues)
    {
        bool hasUrl = source.Url is not null;
        bool hasRelease = source.GithubRelease is not null;

        if (hasUrl == hasRelease)
            issues.Add($"{where}: directUrl needs exactly one of url or githubRelease");
        if (hasUrl && !IsHttps(source.Url!))
            issues.Add($"{where}: directUrl url must be https");
        if (hasRelease && source.GithubRelease!.Repo.Count(c => c == '/') != 1)
            issues.Add($"{where}: githubRelease repo must be owner/name");
        if (string.IsNullOrWhiteSpace(source.Signer))
            issues.Add($"{where}: directUrl needs the expected signer");
        if (source.Sha256 is not null && !IsSha256(source.Sha256))
            issues.Add($"{where}: sha256 must be 64 hex characters");

        foreach (string mirror in source.Mirrors)
        {
            if (!IsHttps(mirror))
                issues.Add($"{where}: mirror '{mirror}' must be https");
        }
    }

    private static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);
}
