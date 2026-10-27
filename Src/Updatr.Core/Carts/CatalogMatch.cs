using Updatr.Core.Catalog;
using Updatr.Core.Sources;

namespace Updatr.Core.Carts;

/// <summary>
/// Which catalog apps are already installed, from a listing the app already has (the Apps screen's)
/// - so the Install screen does not look up hundreds of apps one by one.
/// </summary>
public static class CatalogMatch
{
    /// <summary>Installed if any of the app's sources is: same package identity, case-insensitive.</summary>
    public static bool IsInstalled(CatalogApp app, InstalledIndex installed) => app.Sources.Any(source => source switch
    {
        WingetSourceRef w => installed.Ids.Contains(w.Id),
        NpmSourceRef n => installed.Ids.Contains(n.Package),
        PipxSourceRef p => installed.Ids.Contains(p.Package),
        DotnetToolSourceRef d => installed.Ids.Contains(d.Package),
        PsGallerySourceRef g => installed.Ids.Contains(g.Module),
        VsCodeExtensionSourceRef v => installed.Ids.Contains(v.Extension),

        // A direct download has no package id; its registration carries the product name.
        DirectUrlSourceRef => installed.Names.Any(n => n.StartsWith(app.Name, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    });
}

/// <summary>A listing reduced to what <see cref="CatalogMatch"/> compares.</summary>
public sealed class InstalledIndex
{
    public InstalledIndex(IEnumerable<InstalledPackage> packages)
    {
        List<InstalledPackage> list = [.. packages];
        Ids = new HashSet<string>(list.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        Names = [.. list.Select(p => p.Name)];
    }

    public IReadOnlySet<string> Ids { get; }

    public IReadOnlyList<string> Names { get; }
}
