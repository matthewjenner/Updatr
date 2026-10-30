using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Updatr.Core.Diagnostics;
using Updatr.Core.Operations;
using Updatr.Platform.Net;
using Windows.Management.Deployment;
using WinRtPackageManager = Windows.Management.Deployment.PackageManager;

namespace Updatr.Platform.WinGet;

/// <summary>What the repair did.</summary>
public sealed record WinGetRepairResult(bool Succeeded, string Summary);

/// <summary>
/// Gets winget working on a machine where it is missing or unregistered - fresh images, slimmed
/// ISOs, LTSC. Uses the WinRT package API directly; no PowerShell, no Store.
/// </summary>
/// <remarks>
/// Two steps, cheapest first:
/// <list type="number">
/// <item>Re-register App Installer by family name. On a fresh install it is present but not yet
/// registered for the user until the Store gets round to it; this is the whole fix there.</item>
/// <item>Otherwise download the latest App Installer bundle and its dependencies from winget's own
/// GitHub release and install them for this user. The bundle and dependencies are signed by
/// Microsoft; Windows verifies the signatures as part of installing them.</item>
/// </list>
/// </remarks>
[Operation("winget repair")]
public sealed class RepairWinGet(HttpClient http, WinGetSource source) : IOperation<bool, WinGetRepairResult>
{
    public const string FixId = "winget-repair";
    public const string FamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    /// <summary>The way out when Windows refuses a sideloaded App Installer (often a managed PC's policy).</summary>
    private const string StoreHint = ". Update App Installer from the Microsoft Store instead, then run the checks again.";

    private const string LatestRelease = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";

    /// <param name="request">Ignored.</param>
    public async Task<WinGetRepairResult> RunAsync(bool request, IOperationSink sink, CancellationToken cancellationToken)
    {
        DiagLog.WinGetRepairStep($"starting (App Installer {WinGetRuntime.AppInstallerVersion() ?? "not found"})");
        WinGetRepairResult result = await RepairAsync(sink, cancellationToken).ConfigureAwait(false);
        DiagLog.WinGetRepairStep($"{(result.Succeeded ? "done" : "FAILED")}: {result.Summary} (App Installer now {WinGetRuntime.AppInstallerVersion() ?? "not found"})");
        return result;
    }

    private async Task<WinGetRepairResult> RepairAsync(IOperationSink sink, CancellationToken cancellationToken)
    {
        if (WinGetRuntime.EnsureReady() is { } metadataProblem)
            return new WinGetRepairResult(false, metadataProblem);

        if (Works(out string? version))
            return new WinGetRepairResult(true, $"winget {version} already works; nothing to do.");

        WinRtPackageManager packages = new();

        sink.Report(new StepStarted("Registering App Installer for this user"));
        DiagLog.WinGetRepairStep("registering App Installer by family name");

        try
        {
            await packages.RegisterPackageByFamilyNameAsync(FamilyName, null, DeploymentOptions.None, null, null).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (COMException ex)
        {
            DiagLog.WinGetRepairStep($"registration failed ({ex.Message.Trim()}) - App Installer is probably not present");
        }

        if (Recheck(out version))
            return new WinGetRepairResult(true, $"winget {version} works now (App Installer was present but not registered).");

        return await InstallFromGitHubAsync(packages, sink, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WinGetRepairResult> InstallFromGitHubAsync(WinRtPackageManager packages, IOperationSink sink, CancellationToken cancellationToken)
    {
        sink.Report(new StepStarted("Finding the latest winget release"));
        using JsonDocument release = JsonDocument.Parse(await http.GetStringAsync(LatestRelease, cancellationToken).ConfigureAwait(false));
        string tag = release.RootElement.GetProperty("tag_name").GetString() ?? "latest";

        string? bundleUrl = null, dependenciesUrl = null;

        foreach (JsonElement asset in release.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;

            if (name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
                bundleUrl = url;
            else if (name.Equals("DesktopAppInstaller_Dependencies.zip", StringComparison.OrdinalIgnoreCase))
                dependenciesUrl = url;
        }

        if (bundleUrl is null || dependenciesUrl is null)
            return new WinGetRepairResult(false, $"winget release {tag} does not have the expected App Installer files.");

        // Host temp, not the thumb drive: faster, and installers expect a local path.
        string folder = Path.Combine(Path.GetTempPath(), "Updatr", $"winget-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            string bundle = await DownloadAsync(bundleUrl, folder, "App Installer", sink, cancellationToken).ConfigureAwait(false);
            string zip = await DownloadAsync(dependenciesUrl, folder, "dependencies", sink, cancellationToken).ConfigureAwait(false);

            string extracted = Path.Combine(folder, "deps");
            ZipFile.ExtractToDirectory(zip, extracted);

            // The zip has one folder per architecture (x64, arm64, x86).
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64",
            };

            string archFolder = Path.Combine(extracted, arch);

            if (!Directory.Exists(archFolder))
                return new WinGetRepairResult(false, $"The dependencies in {tag} have no {arch} folder.");

            Uri[] dependencies = [.. Directory.EnumerateFiles(archFolder, "*.appx").Select(f => new Uri(f))];

            sink.Report(new StepStarted($"Installing App Installer {tag} and {dependencies.Length} dependencies"));
            DiagLog.WinGetRepairStep($"installing {Path.GetFileName(bundle)} with {dependencies.Length} {arch} dependencies");

            Progress<DeploymentProgress> installing = new(p => sink.Report(new ProgressReported(null, p.percentage / 100.0, Message: "Installing App Installer")));
            var deployment = packages.AddPackageAsync(new Uri(bundle), dependencies, DeploymentOptions.ForceTargetApplicationShutdown);
            DeploymentResult? result;

            try
            {
                result = await deployment.AsTask(cancellationToken, installing).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A refused deployment throws from AsTask; Windows' own explanation (ErrorText) is
                // only on the operation's result.
                result = TryGetResults(deployment);
                string why = string.IsNullOrWhiteSpace(result?.ErrorText) ? ex.Message.Trim() : result.ErrorText.Trim();
                return new WinGetRepairResult(false, $"Windows refused the App Installer {tag} install: {why} (0x{ex.HResult:X8}"
                    + (result is null ? ")" : $", activity {result.ActivityId})") + StoreHint);
            }

            if (result.ExtendedErrorCode is { } error && error.HResult != 0)
                return new WinGetRepairResult(false, $"Windows refused the App Installer {tag} install: {result.ErrorText} (0x{error.HResult:X8}, activity {result.ActivityId})" + StoreHint);

            return Recheck(out string? version)
                ? new WinGetRepairResult(true, $"Installed winget {version} from {tag}.")
                : new WinGetRepairResult(false, "App Installer installed, but winget still does not answer. Sign out and back in, then run the checks again.");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Windows may still hold the package files briefly; temp cleanup will get them.
            }
        }
    }

    private async Task<string> DownloadAsync(string url, string folder, string what, IOperationSink sink, CancellationToken cancellationToken)
    {
        string path = Path.Combine(folder, Path.GetFileName(new Uri(url).LocalPath));
        sink.Report(new StepStarted($"Downloading {what}"));

        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = File.Create(path);
        byte[] buffer = new byte[81920];
        long done = 0;
        long lastPercent = -1;
        int read;

        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;

            // Once per whole percent: a few hundred MB in 80 KB reads would flood the UI thread.
            long percent = total is > 0 ? done * 100 / total.Value : done / 10485760;

            if (percent != lastPercent)
            {
                lastPercent = percent;
                sink.Report(new ProgressReported(null, total is > 0 ? (double)done / total.Value : null, done, total, $"Downloading {what}"));
            }
        }

        return path;
    }

    private static DeploymentResult? TryGetResults(global::Windows.Foundation.IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> deployment)
    {
        try
        {
            return deployment.GetResults();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private bool Recheck(out string? version)
    {
        source.Reset();
        return Works(out version);
    }

    private static bool Works(out string? version)
    {
        try
        {
            version = WinGetRuntime.CreatePackageManager().Version;
            return true;
        }
        catch (Exception ex) when (WinGetRuntime.IsUnavailable(ex))
        {
            version = null;
            return false;
        }
    }
}
