using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Sources;

namespace Updatr.Platform.Sources;

/// <summary>Global npm packages. npm itself reports what is outdated.</summary>
public sealed class NpmSource(IProcessRunner processes) : ToolSource(processes, "npm")
{
    public override string Id => "npm";

    protected override string DisplayName => "npm";

    protected override async Task<IReadOnlyList<InstalledPackage>> ListAsync(IOperationSink sink, CancellationToken cancellationToken)
    {
        ProcessResult list = await RunToolAsync(["ls", "-g", "--depth=0", "--json"], cancellationToken).ConfigureAwait(false);

        // npm exits 1 when something is outdated - the JSON is still the answer.
        ProcessResult outdated = await RunToolAsync(["outdated", "-g", "--json"], cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> latest = EcosystemParsers.NpmOutdated(outdated.StandardOutput);

        return [.. EcosystemParsers.NpmList(list.StandardOutput).Select(p => Package(p.Name, p.Version, latest.GetValueOrDefault(p.Name)))];
    }

    protected override string? PackageNameOf(PackageSourceRef source) => (source as NpmSourceRef)?.Package;

    protected override IReadOnlyList<string> InstallArguments(string name) => ["install", "-g", name];

    protected override IReadOnlyList<string> UpgradeArguments(InstalledPackage package) => ["install", "-g", $"{package.Id}@latest"];

    protected override IReadOnlyList<string> ReinstallArguments(InstalledPackage package) => ["install", "-g", $"{package.Id}@{package.InstalledVersion}", "--force"];

    protected override IReadOnlyList<string> UninstallArguments(InstalledPackage package) => ["uninstall", "-g", package.Id];
}

/// <summary>Python applications installed with pipx. Latest versions come from PyPI.</summary>
public sealed class PipxSource(IProcessRunner processes, HttpClient http) : ToolSource(processes, "pipx")
{
    public override string Id => "pipx";

    protected override string DisplayName => "pipx";

    protected override async Task<IReadOnlyList<InstalledPackage>> ListAsync(IOperationSink sink, CancellationToken cancellationToken)
    {
        ProcessResult list = await RunToolAsync(["list", "--json"], cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ToolPackage> installed = EcosystemParsers.PipxList(list.StandardOutput);

        IReadOnlyDictionary<string, string?> latest = await LookupAllAsync(installed.Select(p => p.Name), async (name, ct) =>
            EcosystemParsers.PyPiLatest(await http.GetStringAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json", ct).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);

        return [.. installed.Select(p => Package(p.Name, p.Version, latest.GetValueOrDefault(p.Name)))];
    }

    protected override string? PackageNameOf(PackageSourceRef source) => (source as PipxSourceRef)?.Package;

    protected override IReadOnlyList<string> InstallArguments(string name) => ["install", name];

    protected override IReadOnlyList<string> UpgradeArguments(InstalledPackage package) => ["upgrade", package.Id];

    protected override IReadOnlyList<string> ReinstallArguments(InstalledPackage package) => ["reinstall", package.Id];

    protected override IReadOnlyList<string> UninstallArguments(InstalledPackage package) => ["uninstall", package.Id];
}

/// <summary>.NET global tools. Latest versions come from nuget.org; tools from other feeds are listed without update info.</summary>
public sealed class DotnetToolSource(IProcessRunner processes, HttpClient http) : ToolSource(processes, "dotnet")
{
    public override string Id => "dotnetTool";

    protected override string DisplayName => "dotnet tool";

    protected override async Task<IReadOnlyList<InstalledPackage>> ListAsync(IOperationSink sink, CancellationToken cancellationToken)
    {
        ProcessResult list = await RunToolAsync(["tool", "list", "-g"], cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ToolPackage> installed = EcosystemParsers.DotnetToolList(list.StandardOutput);

        IReadOnlyDictionary<string, string?> latest = await LookupAllAsync(installed.Select(p => p.Name), async (name, ct) =>
        {
            using HttpResponseMessage response = await http.GetAsync($"https://api.nuget.org/v3-flatcontainer/{name.ToLowerInvariant()}/index.json", ct).ConfigureAwait(false);

            // 404: not on nuget.org (a private feed, or a tool you built) - no update information.
            return response.IsSuccessStatusCode
                ? EcosystemParsers.NuGetLatestStable(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                : null;
        }, cancellationToken).ConfigureAwait(false);

        return [.. installed.Select(p => latest.GetValueOrDefault(p.Name) is null
            ? Package(p.Name, p.Version, null) with { IsManaged = false, Catalog = "dotnet tool (not on nuget.org)" }
            : Package(p.Name, p.Version, latest[p.Name]))];
    }

    protected override string? PackageNameOf(PackageSourceRef source) => (source as DotnetToolSourceRef)?.Package;

    protected override IReadOnlyList<string> InstallArguments(string name) => ["tool", "install", "-g", name];

    protected override IReadOnlyList<string> UpgradeArguments(InstalledPackage package) => ["tool", "update", "-g", package.Id];

    protected override IReadOnlyList<string> ReinstallArguments(InstalledPackage package)
        => ["tool", "update", "-g", package.Id, "--version", package.InstalledVersion, "--allow-downgrade"];

    protected override IReadOnlyList<string> UninstallArguments(InstalledPackage package) => ["tool", "uninstall", "-g", package.Id];
}

/// <summary>VS Code extensions. Latest versions come from the Marketplace, for this machine's platform.</summary>
public sealed class VsCodeExtensionSource(IProcessRunner processes, HttpClient http) : ToolSource(processes, "code")
{
    private const string MarketplaceQuery = "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";

    public override string Id => "vscode";

    protected override string DisplayName => "VS Code";

    protected override async Task<IReadOnlyList<InstalledPackage>> ListAsync(IOperationSink sink, CancellationToken cancellationToken)
    {
        ProcessResult list = await RunToolAsync(["--list-extensions", "--show-versions"], cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ToolPackage> installed = EcosystemParsers.VsCodeList(list.StandardOutput);

        if (installed.Count == 0)
            return [];

        string platform = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win32-arm64" : "win32-x64";
        IReadOnlyDictionary<string, string> latest;

        try
        {
            latest = await QueryMarketplaceAsync(installed.Select(p => p.Name), platform, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            sink.Report(new LogLine(LogLevel.Warning, $"VS Code Marketplace did not answer ({ex.Message}); extensions are listed without update info"));
            latest = new Dictionary<string, string>();
        }

        return [.. installed.Select(p => Package(p.Name, p.Version, latest.GetValueOrDefault(p.Name)))];
    }

    private async Task<IReadOnlyDictionary<string, string>> QueryMarketplaceAsync(IEnumerable<string> ids, string platform, CancellationToken cancellationToken)
    {
        // Cheap first: only each extension's latest build (flags 0x211 = versions + properties +
        // latest only). Where that build is a pre-release, ask again for those few with their full
        // history (0x11) to find the newest stable - GitLens alone has ~1,500 versions (2 MB).
        EcosystemParsers.MarketplaceResult first = await QueryAsync(ids, 0x211, platform, cancellationToken).ConfigureAwait(false);

        if (first.OnlyPreRelease.Count == 0)
            return first.Latest;

        EcosystemParsers.MarketplaceResult full = await QueryAsync(first.OnlyPreRelease, 0x11, platform, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> merged = new(first.Latest, StringComparer.OrdinalIgnoreCase);

        foreach ((string id, string version) in full.Latest)
            merged[id] = version;

        return merged;
    }

    private async Task<EcosystemParsers.MarketplaceResult> QueryAsync(IEnumerable<string> ids, int flags, string platform, CancellationToken cancellationToken)
    {
        // filterType 7 = extension name (publisher.name).
        JsonObject query = new()
        {
            ["filters"] = new JsonArray(new JsonObject
            {
                ["criteria"] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["filterType"] = 7, ["value"] = id })]),
                ["pageSize"] = 1000,
            }),
            ["flags"] = flags,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, MarketplaceQuery) { Content = JsonContent.Create(query) };
        request.Headers.Accept.ParseAdd("application/json;api-version=3.0-preview.1");

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return EcosystemParsers.VsCodeMarketplace(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), platform);
    }

    protected override string? PackageNameOf(PackageSourceRef source) => (source as VsCodeExtensionSourceRef)?.Extension;

    protected override IReadOnlyList<string> InstallArguments(string name) => ["--install-extension", name];

    protected override IReadOnlyList<string> UpgradeArguments(InstalledPackage package) => ["--install-extension", package.Id, "--force"];

    protected override IReadOnlyList<string> ReinstallArguments(InstalledPackage package) => ["--install-extension", $"{package.Id}@{package.InstalledVersion}", "--force"];

    protected override IReadOnlyList<string> UninstallArguments(InstalledPackage package) => ["--uninstall-extension", package.Id];
}
