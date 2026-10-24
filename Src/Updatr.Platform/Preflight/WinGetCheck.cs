using Microsoft.Management.Deployment;
using Updatr.Core.Preflight;
using Updatr.Platform.WinGet;

namespace Updatr.Platform.Preflight;

/// <summary>Whether the WinGet COM API answers, and which sources it has.</summary>
public sealed class WinGetCheck : IndependentCheck
{
    public override string Id => "winget";

    public override string Title => "winget";

    public override Task<PreflightResult> RunAsync(PreflightContext context, CancellationToken cancellationToken)
    {
        PreflightResult result;

        try
        {
            PackageManager manager = WinGetRuntime.CreatePackageManager();
            IReadOnlyList<PackageCatalogReference> catalogs = manager.GetPackageCatalogs();
            List<string> names = [];

            for (int i = 0; i < catalogs.Count; i++)
                names.Add(catalogs[i].Info.Name);

            context.WinGetAvailable = true;
            result = names.Count == 0
                ? PreflightResult.Warn($"winget {manager.Version}, but no sources are configured", "Reset them with: winget source reset --force")
                : PreflightResult.Pass($"winget {manager.Version}", "Sources: " + string.Join(", ", names));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            context.WinGetAvailable = false;
            result = PreflightResult.Fail(
                "winget is not available",
                $"{ex.Message}\nOn a fresh or slimmed image App Installer may be missing or not yet registered. "
                + "Updates and installs from winget are unavailable until it is installed; other sources and defluff still work.");
        }

        return Task.FromResult(result);
    }
}
