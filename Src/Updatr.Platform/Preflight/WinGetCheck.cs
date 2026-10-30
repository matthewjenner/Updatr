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
        catch (Exception ex) when (WinGetRuntime.IsUnavailable(ex))
        {
            context.WinGetAvailable = false;
            string reason = WinGetRuntime.Describe(ex);
            result = ex is InvalidCastException
                ? PreflightResult.Fail(
                    "winget is too old",
                    $"{reason}.\nA fresh image ships an older App Installer until the Store updates it. Update winget installs the latest "
                    + "from winget's GitHub release; until then Updatr uses the winget command line where it can.",
                    new PreflightFix(RepairWinGet.FixId, "Update winget", "updatr-cli winget repair"))
                : PreflightResult.Fail(
                    "winget is not available",
                    $"{reason}\nOn a fresh or slimmed image App Installer may be missing or not yet registered. "
                    + "Updates and installs from winget are unavailable until it is installed; other sources and debloat still work.",
                    new PreflightFix(RepairWinGet.FixId, "Install winget", "updatr-cli winget repair"));
        }

        return Task.FromResult(result);
    }
}
