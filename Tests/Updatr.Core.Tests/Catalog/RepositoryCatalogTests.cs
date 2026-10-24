using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

/// <summary>
/// The checked-in catalog/ is what every Updatr in the field fetches, so it is held to a stricter
/// standard than runtime reading: unknown properties (typos) fail here.
/// </summary>
public sealed class RepositoryCatalogTests
{
    [Fact]
    public void TheRepositoryCatalogIsValidUnderStrictReading()
    {
        CatalogParseResult result = CatalogLoader.Parse(TestCatalog.RepositoryFiles(), CatalogJson.StrictOptions);

        result.Issues.Should().BeEmpty();
        result.Catalog.Should().NotBeNull();
    }

    [Fact]
    public void EveryFileIsAtTheCurrentSchemaVersion()
    {
        // A bump in CatalogFiles.SchemaVersion must come with the files moving to it; a file left
        // behind still loads, but signals an edit that was not finished.
        foreach (string file in CatalogFiles.All)
        {
            File.ReadAllText(RepositoryPaths.Resolve(Path.Combine("catalog", file)))
                .Should().Contain($"\"schemaVersion\": {CatalogFiles.SchemaVersion}", because: file);
        }
    }

    [Fact]
    public async Task TheEmbeddedCopyIsTheRepositoryCopy()
    {
        CatalogFileSet embedded = (await new EmbeddedCatalogOrigin().ReadAsync(CancellationToken.None))!;
        CatalogFileSet repository = TestCatalog.RepositoryFiles();

        embedded.Files.Keys.Should().BeEquivalentTo(CatalogFiles.All);

        foreach (string file in CatalogFiles.All)
            embedded.Files[file].Should().Be(repository.Files[file], because: file);
    }

    [Fact]
    public void ProvidedToolsCoverEverySourceThatNeedsOne()
    {
        // An npm app in the catalog is useless if nothing in the catalog provides npm: the
        // dependency resolver would have no prerequisite to add to the cart.
        AppCatalog catalog = TestCatalog.LoadRepositoryCatalog();
        HashSet<string> provided = [.. catalog.Apps.SelectMany(a => a.Provides), "winget"];

        IEnumerable<string> unmet = catalog.Apps
            .SelectMany(a => a.Sources.Select(s => (App: a.Id, s.RequiredTool)))
            .Where(x => x.RequiredTool is not null && !provided.Contains(x.RequiredTool))
            .Select(x => $"{x.App} needs '{x.RequiredTool}'");

        unmet.Should().BeEmpty();
    }
}
