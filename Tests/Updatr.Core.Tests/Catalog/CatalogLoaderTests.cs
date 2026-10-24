using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

public sealed class CatalogLoaderTests
{
    private static readonly DateTimeOffset Fetched = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UsesRemoteWhenItIsValidAndCachesIt()
    {
        FakeCache cache = new();
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal(timestamp: Fetched));
        FakeOrigin embedded = new(CatalogOriginKind.Embedded, () => TestCatalog.Minimal());

        CatalogLoadResult result = await new CatalogLoader([remote, cache, embedded], cache).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Remote);
        result.Timestamp.Should().Be(Fetched);
        result.Diagnostics.Should().BeEmpty();
        cache.Stored.Should().NotBeNull();
        embedded.Reads.Should().Be(0);
    }

    [Fact]
    public async Task FallsBackToCacheWhenRemoteThrows()
    {
        FakeCache cache = new(TestCatalog.Minimal(timestamp: Fetched));
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => throw new HttpRequestException("timed out"));

        CatalogLoadResult result = await new CatalogLoader([remote, cache], cache).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Cache);
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("timed out");
    }

    [Fact]
    public async Task AnInvalidRemoteIsNeitherUsedNorCached()
    {
        FakeCache cache = new();
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal(templates: """
            { "schemaVersion": 1, "templates": [ { "id": "dev", "name": "Dev", "items": [ "missing-app" ] } ] }
            """));
        FakeOrigin embedded = new(CatalogOriginKind.Embedded, () => TestCatalog.Minimal());

        CatalogLoadResult result = await new CatalogLoader([remote, cache, embedded], cache).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Embedded);
        result.Diagnostics.Should().Contain(d => d.Contains("unknown app 'missing-app'"));
        cache.Stored.Should().BeNull();
    }

    [Fact]
    public async Task ANewerSchemaIsSkippedAndFlagged()
    {
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal(holds: """{ "schemaVersion": 99, "holds": [] }"""));
        FakeOrigin embedded = new(CatalogOriginKind.Embedded, () => TestCatalog.Minimal());

        CatalogLoadResult result = await new CatalogLoader([remote, embedded]).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Embedded);
        result.NewerSchemaAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task AMissingFileRejectsTheWholeSet()
    {
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => new CatalogFileSet(
            new Dictionary<string, string> { [CatalogFiles.Apps] = TestCatalog.AppsJson("") }, null));
        FakeOrigin embedded = new(CatalogOriginKind.Embedded, () => TestCatalog.Minimal());

        CatalogLoadResult result = await new CatalogLoader([remote, embedded]).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Embedded);
        result.Diagnostics.Single().Should().Contain("templates.json: missing").And.Contain("holds.json: missing");
    }

    [Fact]
    public async Task ACacheWriteFailureDoesNotLoseTheRemoteCatalog()
    {
        FakeCache cache = new(writeFailure: new IOException("drive is read-only"));
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal());

        CatalogLoadResult result = await new CatalogLoader([remote], cache).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Remote);
        result.Diagnostics.Should().ContainSingle().Which.Should().Contain("read-only");
    }

    [Fact]
    public async Task UnknownPropertiesAreIgnoredAtRuntime()
    {
        // An older build reading a newer catalog that only added optional fields.
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal(apps: TestCatalog.AppsJson("""
            { "id": "git", "name": "Git", "category": "dev", "futureField": 1,
              "sources": [ { "id": "Git.Git", "$type": "winget", "alsoNew": true } ] }
            """)));

        CatalogLoadResult result = await new CatalogLoader([remote]).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Remote);
        result.Catalog.FindApp("git")!.Sources.Single().Should().BeOfType<WingetSourceRef>();
    }

    [Fact]
    public async Task NoUsableOriginThrowsWithEveryReason()
    {
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => throw new HttpRequestException("offline"));
        FakeOrigin cache = new(CatalogOriginKind.Cache, () => null);

        Func<Task> load = () => new CatalogLoader([remote, cache]).LoadAsync(CancellationToken.None);

        (await load.Should().ThrowAsync<CatalogUnavailableException>())
            .Which.Diagnostics.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        FakeOrigin remote = new(CatalogOriginKind.Remote, () => TestCatalog.Minimal());

        Func<Task> load = () => new CatalogLoader([remote]).LoadAsync(cts.Token);

        await load.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task TheEmbeddedCatalogLoads()
    {
        CatalogLoadResult result = await new CatalogLoader([new EmbeddedCatalogOrigin()]).LoadAsync(CancellationToken.None);

        result.Origin.Should().Be(CatalogOriginKind.Embedded);
        result.Catalog.Apps.Should().NotBeEmpty();
        result.Catalog.Holds.Should().NotBeEmpty();
    }
}
