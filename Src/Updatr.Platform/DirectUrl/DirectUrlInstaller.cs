using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.Elevation;
using Updatr.Platform.Net;

namespace Updatr.Platform.DirectUrl;

/// <summary>
/// Installs an app straight from its vendor: resolve the URL, download with retries and mirrors,
/// verify (sha256 when given, Authenticode signer always), run the installer silently.
/// </summary>
/// <remarks>
/// Downloads go to the host's temp folder, never the thumb drive. Installers that write
/// machine-wide (MSI, Inno, Nullsoft, Burn, exe) run in the elevated broker, which re-checks the
/// signature itself before running anything - it never trusts the unelevated side's word.
/// </remarks>
public sealed class DirectUrlInstaller(HttpClient http, IProcessRunner processes, ElevationService? elevation)
{
    public const string BrokerRunType = "directurl-run";

    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    public async Task<PackageOutcome> InstallAsync(DirectUrlSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        if (source.InstallerType is InstallerType.Zip)
            return PackageOutcome.Skipped("Zip (portable) apps arrive with the Install screen's carts");

        if (source.InstallerType is InstallerType.Exe && string.IsNullOrWhiteSpace(source.SilentArgs))
            return PackageOutcome.Failed("The catalog gives no silent switches for this exe installer");

        string folder = Path.Combine(Path.GetTempPath(), "Updatr", $"direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            sink.Report(new ProgressReported(itemId, null, Message: "Finding the download"));
            IReadOnlyList<string> urls = await ResolveAsync(source, cancellationToken).ConfigureAwait(false);
            string file = Path.Combine(folder, FileNameFrom(urls[0]));

            await new Downloader(http).DownloadAsync(urls, file, sink, itemId, cancellationToken).ConfigureAwait(false);
            sink.Report(new ProgressReported(itemId, null, Message: "Verifying"));

            if (Verify(file, source) is { } problem)
                return PackageOutcome.Failed($"Not installed: {problem}");

            sink.Report(new ProgressReported(itemId, null, Message: "Installing"));
            return await RunAsync(file, source, itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadFailedException ex)
        {
            return PackageOutcome.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return PackageOutcome.Failed($"Could not find the download: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // An installer may still hold its file briefly; temp cleanup gets it later.
            }
        }
    }

    /// <summary>The primary URL (fixed, or the matching asset of the latest GitHub release), then mirrors.</summary>
    public async Task<IReadOnlyList<string>> ResolveAsync(DirectUrlSourceRef source, CancellationToken cancellationToken)
    {
        string primary;

        if (source.Url is { } fixedUrl)
        {
            primary = fixedUrl;
        }
        else if (source.GithubRelease is { } release)
        {
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{release.Repo}/releases/latest", cancellationToken).ConfigureAwait(false));
            WildcardPattern pattern = new(release.AssetPattern);

            primary = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Where(a => pattern.IsMatch(a.GetProperty("name").GetString() ?? string.Empty))
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .OfType<string>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"no asset in {release.Repo}'s latest release matches '{release.AssetPattern}'");
        }
        else
        {
            throw new InvalidOperationException("the catalog entry has neither url nor githubRelease");
        }

        return [primary, .. source.Mirrors];
    }

    /// <summary>Null when the file may run; otherwise why not.</summary>
    public static string? Verify(string file, DirectUrlSourceRef source)
    {
        if (source.Sha256 is { } expected)
        {
            using FileStream stream = File.OpenRead(file);
            string actual = Convert.ToHexString(SHA256.HashData(stream));

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                return $"its SHA-256 is {actual}, not {expected} as the catalog expects";
        }

        // MSIX signatures are enforced by Windows when the package is added.
        return source.InstallerType is InstallerType.Msix ? null : Authenticode.Require(file, source.Signer);
    }

    /// <summary>The program and arguments that run <paramref name="file"/> silently.</summary>
    public static (string Program, IReadOnlyList<string> Arguments) Command(string file, InstallerType type, string? silentArgs)
    {
        IReadOnlyList<string> extra = CommandLine.Split(silentArgs);

        return type switch
        {
            InstallerType.Msi or InstallerType.Wix =>
                (Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/i", file, .. (extra.Count > 0 ? extra : ["/qn", "/norestart"])]),
            InstallerType.Inno => (file, extra.Count > 0 ? extra : ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"]),
            InstallerType.Nullsoft => (file, extra.Count > 0 ? extra : ["/S"]),
            InstallerType.Burn => (file, extra.Count > 0 ? extra : ["/quiet", "/norestart"]),
            _ => (file, extra),
        };
    }

    private async Task<PackageOutcome> RunAsync(string file, DirectUrlSourceRef source, string itemId, CancellationToken cancellationToken)
    {
        if (source.InstallerType is InstallerType.Msix)
        {
            global::Windows.Management.Deployment.PackageManager packages = new();
            global::Windows.Management.Deployment.DeploymentResult result = await packages.AddPackageAsync(new Uri(file), null, global::Windows.Management.Deployment.DeploymentOptions.ForceTargetApplicationShutdown)
                .AsTask(cancellationToken).ConfigureAwait(false);
            return result.ExtendedErrorCode is { HResult: not 0 } error ? PackageOutcome.FromCode(error.HResult) : PackageOutcome.Ok();
        }

        BrokerClient? broker = elevation is null ? null : await elevation.GetBrokerAsync(cancellationToken).ConfigureAwait(false);

        if (broker is not null)
        {
            JsonObject request = new() { ["file"] = file, ["type"] = source.InstallerType.ToString(), ["args"] = source.SilentArgs, ["signer"] = source.Signer, ["sha256"] = source.Sha256 };

            try
            {
                JsonNode? reply = await broker.SendAsync(BrokerRunType, request, null, CancellationToken.None).ConfigureAwait(false);
                return reply?["error"]?.GetValue<string>() is { } refused
                    ? PackageOutcome.Failed(refused)
                    : PackageOutcome.FromCode(reply?["exitCode"]?.GetValue<int>() ?? -1);
            }
            catch (BrokerException ex)
            {
                return PackageOutcome.Failed($"The elevated helper failed: {ex.Message}");
            }
        }

        // No broker (declined): run it here - the installer may ask for elevation itself.
        (string program, IReadOnlyList<string> arguments) = Command(file, source.InstallerType, source.SilentArgs);
        ProcessResult run = await processes.RunAsync(program, arguments, InstallTimeout, CancellationToken.None).ConfigureAwait(false);
        return PackageOutcome.FromCode(run.ExitCode);
    }

    private static string FileNameFrom(string url)
    {
        string name = Path.GetFileName(new Uri(url).LocalPath);
        return string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "installer.exe" : name;
    }
}

/// <summary>
/// Runs a downloaded installer elevated. Re-verifies the signature (and hash) in the broker before
/// running: the file sits in a user-writable folder, so the unelevated check is not enough.
/// </summary>
public sealed class DirectUrlBrokerHandler : IBrokerHandler
{
    public string Type => DirectUrlInstaller.BrokerRunType;

    public async Task<JsonNode?> HandleAsync(JsonNode? payload, Func<JsonNode, Task> emit, CancellationToken cancellationToken)
    {
        string file = payload?["file"]?.GetValue<string>() ?? throw new ArgumentException("Missing file.");
        InstallerType type = Enum.Parse<InstallerType>(payload?["type"]?.GetValue<string>() ?? throw new ArgumentException("Missing type."));
        string signer = payload?["signer"]?.GetValue<string>() ?? throw new ArgumentException("Missing signer.");

        // Only files Updatr downloaded into its own temp folders.
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Updatr")) + Path.DirectorySeparatorChar;

        if (!Path.GetFullPath(file).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Refusing to run a file outside Updatr's download folder.");

        DirectUrlSourceRef source = new() { InstallerType = type, Signer = signer, Sha256 = payload?["sha256"]?.GetValue<string>(), SilentArgs = payload?["args"]?.GetValue<string>() };

        if (DirectUrlInstaller.Verify(file, source) is { } problem)
            return new JsonObject { ["error"] = $"Not installed: {problem}" };

        (string program, IReadOnlyList<string> arguments) = DirectUrlInstaller.Command(file, type, source.SilentArgs);
        ProcessResult result = await new Processes.ProcessRunner().RunAsync(program, arguments, TimeSpan.FromMinutes(45), CancellationToken.None).ConfigureAwait(false);
        return new JsonObject { ["exitCode"] = result.ExitCode };
    }
}
