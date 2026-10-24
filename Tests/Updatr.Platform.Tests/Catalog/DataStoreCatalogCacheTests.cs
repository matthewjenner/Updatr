using Updatr.Core.Catalog;
using Updatr.Platform.Catalog;
using Updatr.Platform.Data;

namespace Updatr.Platform.Tests.Catalog;

public sealed class DataStoreCatalogCacheTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PortableDataStore _store;
    private readonly DataStoreCatalogCache _cache;

    public DataStoreCatalogCacheTests()
    {
        _store = new PortableDataStore(_temp.Path);
        _cache = new DataStoreCatalogCache(_store);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogFileSet Set(string marker, DateTimeOffset? at = null)
        => new(CatalogFiles.All.ToDictionary(f => f, f => $"{f}:{marker}"), at);

    [Fact]
    public async Task AnEmptyCacheReadsAsNothing()
    {
        (await _cache.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task AWrittenSetReadsBackWithItsTimestamp()
    {
        DateTimeOffset fetched = new(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(-5));
        await _cache.WriteAsync(Set("v1", fetched), Ct);

        CatalogFileSet? read = await _cache.ReadAsync(Ct);

        read!.Files[CatalogFiles.Apps].Should().Be("apps.json:v1");
        read.Timestamp.Should().Be(fetched);
    }

    [Fact]
    public async Task ASetInterruptedMidWriteIsIgnored()
    {
        await _cache.WriteAsync(Set("v1"), Ct);

        // Simulate the drive being pulled after the marker was removed and one file rewritten.
        await _store.DeleteAsync($"{DataStoreCatalogCache.Folder}/fetched.txt", Ct);
        await _store.WriteTextAsync($"{DataStoreCatalogCache.Folder}/{CatalogFiles.Apps}", "apps.json:v2", Ct);

        (await _cache.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task AMissingFileMakesTheSetUnusable()
    {
        await _cache.WriteAsync(Set("v1"), Ct);
        await _store.DeleteAsync($"{DataStoreCatalogCache.Folder}/{CatalogFiles.Holds}", Ct);

        (await _cache.ReadAsync(Ct)).Should().BeNull();
    }
}
