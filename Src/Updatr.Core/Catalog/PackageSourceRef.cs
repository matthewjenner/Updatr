using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Updatr.Core.Catalog;

/// <summary>
/// One way to get an app: which ecosystem, and the app's identity there. An app lists its sources
/// in preference order.
/// </summary>
/// <remarks>
/// The <c>$type</c> discriminators are user-visible (people hand-edit the catalog and their own
/// carts) - do not rename them.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WingetSourceRef), "winget")]
[JsonDerivedType(typeof(NpmSourceRef), "npm")]
[JsonDerivedType(typeof(PipxSourceRef), "pipx")]
[JsonDerivedType(typeof(DotnetToolSourceRef), "dotnetTool")]
[JsonDerivedType(typeof(PsGallerySourceRef), "psGallery")]
[JsonDerivedType(typeof(VsCodeExtensionSourceRef), "vscode")]
[JsonDerivedType(typeof(DirectUrlSourceRef), "directUrl")]
public abstract record PackageSourceRef
{
    // Computed in the base, not overridden per type: [JsonIgnore] on an abstract member does not
    // carry over to overrides, which then leak into the JSON (and the generated schema).

    /// <summary>
    /// The tool this source needs on the machine (matched against <see cref="CatalogApp.Provides"/>),
    /// or null when Updatr handles the source natively.
    /// </summary>
    [JsonIgnore]
    public string? RequiredTool => this switch
    {
        WingetSourceRef => "winget",
        NpmSourceRef => "npm",
        PipxSourceRef => "pipx",
        DotnetToolSourceRef => "dotnet",
        VsCodeExtensionSourceRef => "code",
        _ => null,
    };

    /// <summary>Short source id used in the UI, logs, and hold rules ("winget", "msstore", "npm", ...).</summary>
    [JsonIgnore]
    public string SourceId => this switch
    {
        WingetSourceRef w => w.Source,
        NpmSourceRef => "npm",
        PipxSourceRef => "pipx",
        DotnetToolSourceRef => "dotnetTool",
        PsGallerySourceRef => "psGallery",
        VsCodeExtensionSourceRef => "vscode",
        DirectUrlSourceRef => "directUrl",
        _ => throw new NotSupportedException(GetType().Name),
    };
}

[Description("A winget package.")]
public sealed record WingetSourceRef : PackageSourceRef
{
    [Description("winget package id, e.g. Git.Git. Matched exactly.")]
    public required string Id { get; init; }

    [Description("winget source name. Defaults to \"winget\"; use \"msstore\" for Microsoft Store apps.")]
    public string Source { get; init => field = value ?? "winget"; } = "winget";
}

[Description("A global npm package.")]
public sealed record NpmSourceRef : PackageSourceRef
{
    [Description("npm package name, e.g. typescript or @scope/name.")]
    public required string Package { get; init; }
}

[Description("A Python application installed with pipx.")]
public sealed record PipxSourceRef : PackageSourceRef
{
    [Description("PyPI package name.")]
    public required string Package { get; init; }
}

[Description("A .NET global tool.")]
public sealed record DotnetToolSourceRef : PackageSourceRef
{
    [Description("NuGet package id of the tool, e.g. dotnet-ef.")]
    public required string Package { get; init; }
}

[Description("A PowerShell Gallery module. Installed natively from the NuGet feed; no PowerShell needed.")]
public sealed record PsGallerySourceRef : PackageSourceRef
{
    [Description("Module name as published on the PowerShell Gallery.")]
    public required string Module { get; init; }
}

[Description("A VS Code extension.")]
public sealed record VsCodeExtensionSourceRef : PackageSourceRef
{
    [Description("Marketplace extension id, publisher.name, e.g. ms-dotnettools.csharp.")]
    public required string Extension { get; init; }
}

[Description("An installer downloaded straight from the vendor.")]
public sealed record DirectUrlSourceRef : PackageSourceRef
{
    [Description("Fixed https download URL. Set this or githubRelease, not both.")]
    public string? Url { get; init; }

    [Description("Resolve the latest release of a GitHub repository instead of a fixed URL.")]
    public GitHubReleaseRef? GithubRelease { get; init; }

    [Description("Installer technology, which decides how it is run.")]
    public required InstallerType InstallerType { get; init; }

    [Description("Arguments for a silent, no-reboot install. Omit to use the installer type's default.")]
    public string? SilentArgs { get; init; }

    [Description("Expected Authenticode signer (certificate subject CN). The download is rejected if it does not match.")]
    public required string Signer { get; init; }

    [Description("Optional SHA-256 of the installer. Only for installers whose URL never changes content.")]
    public string? Sha256 { get; init; }

    [Description("Fallback URLs tried in order when the primary fails. Reserved for a self-hosted mirror; normally empty.")]
    public IReadOnlyList<string> Mirrors { get; init => field = value ?? []; } = [];
}

[Description("Pick an asset from a GitHub repository's latest release.")]
public sealed record GitHubReleaseRef
{
    [Description("owner/name, e.g. git-for-windows/git.")]
    public required string Repo { get; init; }

    [Description("Wildcard matched against asset file names (* and ?), e.g. Git-*-64-bit.exe.")]
    public required string AssetPattern { get; init; }
}

/// <summary>Installer technologies a direct-URL source can run. JSON names are user-visible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InstallerType>))]
public enum InstallerType
{
    [JsonStringEnumMemberName("msi")] Msi,
    [JsonStringEnumMemberName("msix")] Msix,
    [JsonStringEnumMemberName("exe")] Exe,
    [JsonStringEnumMemberName("inno")] Inno,
    [JsonStringEnumMemberName("nullsoft")] Nullsoft,
    [JsonStringEnumMemberName("wix")] Wix,
    [JsonStringEnumMemberName("burn")] Burn,
    [JsonStringEnumMemberName("zip")] Zip,
}
