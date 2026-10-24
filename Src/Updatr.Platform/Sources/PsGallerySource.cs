using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.Net;

namespace Updatr.Platform.Sources;

/// <summary>
/// PowerShell Gallery modules - handled natively, no PowerShell involved. Lists modules that
/// PowerShellGet / PSResourceGet installed from PSGallery (they carry a <c>PSGetModuleInfo.xml</c>);
/// inbox modules and other repositories are left alone.
/// </summary>
/// <remarks>
/// Upgrading installs the new version side by side, like <c>Update-Module</c>: the package is
/// downloaded from the gallery and extracted into <c>&lt;root&gt;\&lt;Name&gt;\&lt;Version&gt;\</c>.
/// PowerShell loads the highest version. Modules under Program Files (all users) are installed by
/// the elevated broker, which only accepts those known roots.
/// </remarks>
public sealed class PsGallerySource(HttpClient http, ElevationService? elevation) : IPackageSource
{
    public const string SourceId = "psGallery";
    public const string BrokerInstallType = "psgallery-install";
    private const string Gallery = "https://www.powershellgallery.com/api/v2";

    public string Id => SourceId;

    /// <summary>Where gallery modules live: (folder, true when it needs admin to write).</summary>
    public static IReadOnlyList<(string Root, bool AllUsers)> ModuleRoots()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        return
        [
            (Path.Combine(documents, "PowerShell", "Modules"), false),
            (Path.Combine(documents, "WindowsPowerShell", "Modules"), false),
            (Path.Combine(programFiles, "PowerShell", "Modules"), true),
            (Path.Combine(programFiles, "WindowsPowerShell", "Modules"), true),
        ];
    }

    public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<InstalledModule> modules = [.. FindModules()];

        if (modules.Count == 0)
            yield break;

        Dictionary<string, string?> latest = new(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(modules.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (name, ct) =>
        {
            string? version = await LatestAsync(name, ct).ConfigureAwait(false);

            lock (latest)
                latest[name] = version;
        }).ConfigureAwait(false);

        foreach (InstalledModule module in modules)
            yield return ToPackage(module, latest.GetValueOrDefault(module.Name));
    }

    public async Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
    {
        InstalledModule? module = FindModules().FirstOrDefault(m => string.Equals(m.Name, id, StringComparison.OrdinalIgnoreCase));
        return module is null ? null : ToPackage(module, await LatestAsync(module.Name, cancellationToken).ConfigureAwait(false));
    }

    public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => InstallAsync(package, package.AvailableVersion ?? package.InstalledVersion, sink);

    public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        => InstallAsync(package, package.InstalledVersion, sink);

    private async Task<PackageOutcome> InstallAsync(InstalledPackage package, string version, IOperationSink sink)
    {
        InstalledModule? module = FindModules().FirstOrDefault(m => string.Equals(m.Name, package.Id, StringComparison.OrdinalIgnoreCase));

        if (module is null)
            return PackageOutcome.Failed("The module is no longer installed");

        if (!module.AllUsers)
            return await InstallIntoAsync(http, module.Name, version, module.Root, sink, package.Key.ToString()).ConfigureAwait(false);

        BrokerClient? broker = elevation is null ? null : await elevation.GetBrokerAsync(CancellationToken.None).ConfigureAwait(false);

        if (broker is null)
            return PackageOutcome.Skipped("Installed for all users - needs administrator access, which was declined");

        try
        {
            JsonNode? reply = await broker.SendAsync(BrokerInstallType,
                new JsonObject { ["name"] = module.Name, ["version"] = version, ["root"] = module.Root }, null, CancellationToken.None).ConfigureAwait(false);
            return reply?["ok"]?.GetValue<bool>() == true ? PackageOutcome.Ok() : PackageOutcome.Failed(reply?["detail"]?.GetValue<string>() ?? "failed");
        }
        catch (BrokerException ex)
        {
            return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads <paramref name="name"/> <paramref name="version"/> from the gallery and extracts it
    /// to <c>root\name\version</c>. Also what the broker runs for all-users modules.
    /// </summary>
    public static async Task<PackageOutcome> InstallIntoAsync(HttpClient http, string name, string version, string root, IOperationSink sink, string itemId)
    {
        if (!EcosystemParsers.IsSafePackageName(name) || !EcosystemParsers.IsSafeVersion(version))
            return PackageOutcome.Failed("Unexpected characters in the module name or version");

        string temp = Path.Combine(Path.GetTempPath(), "Updatr", $"psgallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            string nupkg = Path.Combine(temp, $"{name}.{version}.nupkg");
            await new Downloader(http).DownloadAsync([$"{Gallery}/package/{name}/{version}"], nupkg, sink, itemId, CancellationToken.None).ConfigureAwait(false);

            using ZipArchive zip = ZipFile.OpenRead(nupkg);

            if (UnmetDependencies(zip) is { Count: > 0 } missing)
                return PackageOutcome.Failed($"Needs modules that are not installed: {string.Join(", ", missing)}");

            string moduleFolder = Path.Combine(root, name);
            string target = Path.Combine(moduleFolder, version);
            string staging = Path.Combine(moduleFolder, $".updatr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                // NuGet packaging files are not part of the module.
                string path = Uri.UnescapeDataString(entry.FullName);

                if (path.EndsWith('/') || path == "[Content_Types].xml" || path.StartsWith("_rels/", StringComparison.Ordinal)
                    || path.StartsWith("package/", StringComparison.Ordinal) || path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destination = Path.GetFullPath(Path.Combine(staging, path));

                // Zip-slip guard: an entry like "../../evil.ps1" must not escape the module folder.
                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return PackageOutcome.Failed($"The package contains an unsafe path ({path})");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            CarryOverPsGetInfo(moduleFolder, staging, version);

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true); // Reinstall of the same version.

            Directory.Move(staging, target);
            return PackageOutcome.Ok();
        }
        catch (Exception ex) when (ex is DownloadFailedException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return PackageOutcome.Failed(ex is UnauthorizedAccessException or IOException
                ? $"{ex.Message} (close PowerShell sessions that have the module loaded, then retry)"
                : ex.Message);
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

    private async Task<string?> LatestAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            string xml = await http.GetStringAsync($"{Gallery}/FindPackagesById()?id='{Uri.EscapeDataString(name)}'&$filter=IsLatestVersion", cancellationToken).ConfigureAwait(false);
            return EcosystemParsers.PsGalleryLatest(xml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static InstalledPackage ToPackage(InstalledModule module, string? latest) => new()
    {
        Source = SourceId,
        Id = module.Name,
        Name = module.Name,
        InstalledVersion = module.Version,
        AvailableVersion = VersionComparer.IsNewer(latest, module.Version) ? latest : null,
        Catalog = "PSGallery",
        Scope = module.AllUsers ? InstallScope.Machine : InstallScope.User,
        RunsElevated = module.AllUsers,
    };

    /// <summary>Gallery-installed modules: highest version folder per module, per root.</summary>
    private static IEnumerable<InstalledModule> FindModules()
    {
        foreach ((string root, bool allUsers) in ModuleRoots())
        {
            if (!Directory.Exists(root))
                continue;

            foreach (string moduleFolder in Directory.EnumerateDirectories(root))
            {
                List<string> versions = [];
                bool fromGallery = false;

                foreach (string versionFolder in Directory.EnumerateDirectories(moduleFolder))
                {
                    string info = Path.Combine(versionFolder, "PSGetModuleInfo.xml");
                    string version = Path.GetFileName(versionFolder);

                    if (version.StartsWith('.'))
                        continue; // An interrupted Updatr staging folder.

                    versions.Add(version);

                    if (File.Exists(info) && EcosystemParsers.PsGetRepository(File.ReadAllText(info)) == "PSGallery")
                        fromGallery = true;
                }

                if (fromGallery && versions.Count > 0)
                    yield return new InstalledModule(Path.GetFileName(moduleFolder), versions.OrderBy(v => v, VersionComparer.Instance).Last(), root, allUsers);
            }
        }
    }

    /// <summary>Required modules listed in the package's nuspec that are not installed in any root.</summary>
    private static List<string> UnmetDependencies(ZipArchive zip)
    {
        ZipArchiveEntry? nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/', StringComparison.Ordinal));

        if (nuspec is null)
            return [];

        using Stream stream = nuspec.Open();
        XDocument doc = XDocument.Load(stream);
        HashSet<string> installed = new(ModuleRoots().Where(r => Directory.Exists(r.Root))
            .SelectMany(r => Directory.EnumerateDirectories(r.Root).Select(Path.GetFileName))
            .OfType<string>(), StringComparer.OrdinalIgnoreCase);

        return [.. doc.Descendants().Where(e => e.Name.LocalName == "dependency")
            .Select(e => e.Attribute("id")?.Value)
            .OfType<string>()
            .Where(id => !installed.Contains(id))];
    }

    /// <summary>
    /// Copies the previous version's PSGetModuleInfo.xml into the new version (version and path
    /// updated), so PowerShellGet's own commands still see the module as gallery-installed.
    /// </summary>
    private static void CarryOverPsGetInfo(string moduleFolder, string staging, string newVersion)
    {
        string? previous = Directory.EnumerateDirectories(moduleFolder)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && File.Exists(Path.Combine(d, "PSGetModuleInfo.xml")))
            .OrderBy(d => Path.GetFileName(d), VersionComparer.Instance)
            .LastOrDefault();

        if (previous is null)
            return;

        string oldVersion = Path.GetFileName(previous);
        string now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture);
        string text = File.ReadAllText(Path.Combine(previous, "PSGetModuleInfo.xml"))
            .Replace(previous, Path.Combine(moduleFolder, newVersion), StringComparison.OrdinalIgnoreCase)
            .Replace($">{oldVersion}<", $">{newVersion}<", StringComparison.Ordinal);

        // Dates are CLIXML <DT N="InstalledDate">...</DT>; stamp this install, not the old one.
        text = Regex.Replace(text, "<DT N=\"(InstalledDate|UpdatedDate)\">[^<]*</DT>", m => $"<DT N=\"{m.Groups[1].Value}\">{now}</DT>");

        File.WriteAllText(Path.Combine(staging, "PSGetModuleInfo.xml"), text);
    }

    private sealed record InstalledModule(string Name, string Version, string Root, bool AllUsers);
}

/// <summary>Installs an all-users gallery module inside the elevated broker. Only known module roots are accepted.</summary>
public sealed class PsGalleryBrokerHandler : IBrokerHandler
{
    public string Type => PsGallerySource.BrokerInstallType;

    public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
    {
        string name = payload?["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing module name.");
        string version = payload?["version"]?.GetValue<string>() ?? throw new ArgumentException("Missing version.");
        string root = payload?["root"]?.GetValue<string>() ?? throw new ArgumentException("Missing root.");

        // The broker writes as administrator: refuse anything but the all-users module folders.
        bool known = PsGallerySource.ModuleRoots().Any(r => r.AllUsers && string.Equals(Path.GetFullPath(r.Root), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));

        if (!known)
            throw new ArgumentException($"'{root}' is not an all-users PowerShell module folder.");

        PackageOutcome outcome = await PsGallerySource.InstallIntoAsync(Net.UpdatrHttp.Client, name, version, root, new SilentSink(), name).ConfigureAwait(false);
        return new JsonObject { ["ok"] = outcome.Kind == OutcomeKind.Ok, ["detail"] = outcome.Detail };
    }

    private sealed class SilentSink : IOperationSink
    {
        public void Report(OperationEvent operationEvent)
        {
        }

        public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
