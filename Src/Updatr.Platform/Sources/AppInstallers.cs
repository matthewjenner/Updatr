using Updatr.Core.Abstractions;
using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Platform.DirectUrl;
using Updatr.Platform.WinGet;

namespace Updatr.Platform.Sources;

/// <summary>Installs catalog apps whose source is winget (or the Microsoft Store through winget).</summary>
public sealed class WinGetAppInstaller(WinGetSource winget, IProcessRunner processes) : IAppInstaller
{
    public bool Handles(PackageSourceRef source) => source is WingetSourceRef;

    public async Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken)
    {
        try
        {
            return await winget.FindInstalledAsync(((WingetSourceRef)source).Id, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (PackageSourceException)
        {
            return false; // Cannot tell - try the install; winget says "already installed" if it is.
        }
    }

    public Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => winget.InstallNewAsync((WingetSourceRef)source, itemId, sink, processes);
}

/// <summary>Installs catalog apps straight from their vendor (see <see cref="DirectUrlInstaller"/>).</summary>
public sealed class DirectUrlAppInstaller(DirectUrlInstaller installer) : IAppInstaller
{
    public bool Handles(PackageSourceRef source) => source is DirectUrlSourceRef;

    /// <summary>A direct download has no package identity to look up; installers handle "already installed" themselves.</summary>
    public Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        => installer.InstallAsync((DirectUrlSourceRef)source, itemId, sink, cancellationToken);
}

/// <summary>Installs PowerShell Gallery modules for the current user (PowerShell 7's user module folder).</summary>
public sealed class PsGalleryAppInstaller(HttpClient http, PsGallerySource gallery) : IAppInstaller
{
    public bool Handles(PackageSourceRef source) => source is PsGallerySourceRef;

    public async Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken)
        => await gallery.FindInstalledAsync(((PsGallerySourceRef)source).Module, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
    {
        string module = ((PsGallerySourceRef)source).Module;
        string? version = await gallery.LatestVersionAsync(module, cancellationToken).ConfigureAwait(false);

        if (version is null)
            return PackageOutcome.Failed($"The PowerShell Gallery has no module '{module}' (or did not answer)");

        string root = PsGallerySource.ModuleRoots()[0].Root; // Documents\PowerShell\Modules
        return await PsGallerySource.InstallIntoAsync(http, module, version, root, sink, itemId).ConfigureAwait(false);
    }
}
