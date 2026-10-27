using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Catalog;

public sealed class CatalogSectionsTests
{
    private static CatalogApp App(string id, string category, params PackageSourceRef[] sources)
        => new() { Id = id, Name = id, Category = category, Sources = sources };

    [Fact]
    public void AVsCodeExtensionGetsItsOwnSectionWhateverItsCategory()
    {
        CatalogSections.SectionOf(App("gitlens", "dev", new VsCodeExtensionSourceRef { Extension = "eamodio.gitlens" }))
            .Should().Be(CatalogSections.VsCodeExtensions);

        CatalogSections.SectionOf(App("git", "dev", new WingetSourceRef { Id = "Git.Git" })).Should().Be("dev");
    }

    [Fact]
    public void KnownSectionsComeFirstThenUnknownThenApart()
    {
        CatalogSections.Order("browsers").Should().BeLessThan(CatalogSections.Order("runtimes"));
        CatalogSections.Order("runtimes").Should().BeLessThan(CatalogSections.Order("something-new"));
        CatalogSections.Order("something-new").Should().BeLessThan(CatalogSections.Order(CatalogSections.VsCodeExtensions));
        CatalogSections.Describe("something-new").Title.Should().Be("Something-new");
    }

    [Fact]
    public void EverySectionInTheRepositoryCatalogHasATitle()
    {
        // A new category in apps.json should get a proper name and place here, not "Xyz" at the end.
        AppCatalog catalog = TestCatalog.LoadRepositoryCatalog();

        catalog.Apps.Select(CatalogSections.SectionOf).Distinct()
            .Should().OnlyContain(key => CatalogSections.Known.Any(s => s.Key == key));
    }

    [Fact]
    public void InstalledIsMatchedOnAnySourcesIdentity()
    {
        InstalledIndex installed = new(
        [
            Pkg("winget", "Mozilla.Firefox", "Mozilla Firefox (x64 en-US)"),
            Pkg("npm", "typescript", "typescript"),
            Pkg("winget", @"ARP\Machine\X64\RustDesk", "RustDesk"),
            Pkg("winget", "9NKSQGP7F2NH", "WhatsApp"),
        ]);

        CatalogMatch.IsInstalled(App("firefox", "browsers", new WingetSourceRef { Id = "mozilla.firefox" }), installed).Should().BeTrue();
        CatalogMatch.IsInstalled(App("typescript", "dev", new NpmSourceRef { Package = "typescript" }), installed).Should().BeTrue();
        CatalogMatch.IsInstalled(App("whatsapp", "messaging", new WingetSourceRef { Id = "9NKSQGP7F2NH", Source = "msstore" }), installed).Should().BeTrue();
        CatalogMatch.IsInstalled(new CatalogApp
        {
            Id = "rustdesk", Name = "RustDesk", Category = "remote",
            Sources = [new DirectUrlSourceRef { GithubRelease = new() { Repo = "rustdesk/rustdesk", AssetPattern = "*.msi" }, InstallerType = InstallerType.Msi, Signer = "PURSLANE" }],
        }, installed).Should().BeTrue();
        CatalogMatch.IsInstalled(App("chrome", "browsers", new WingetSourceRef { Id = "Google.Chrome" }), installed).Should().BeFalse();
    }

    private static InstalledPackage Pkg(string source, string id, string name) => new() { Source = source, Id = id, Name = name, InstalledVersion = "1.0" };
}
