using Updatr.Core.Abstractions;
using Updatr.Platform.Data;

namespace Updatr.Platform.Tests.Data;

public sealed class PortableDataStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PortableDataStore _store;

    public PortableDataStoreTests()
    {
        _store = new PortableDataStore(Path.Combine(_temp.Path, "Updatr.data"));
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WritesCreateFoldersAndReadBack()
    {
        await _store.WriteTextAsync("carts/dev.json", "{ \"a\": 1 }", Ct);

        (await _store.ReadTextAsync("carts/dev.json", Ct)).Should().Be("{ \"a\": 1 }");
    }

    [Fact]
    public async Task AWriteReplacesTheOldContentAndLeavesNoTempFiles()
    {
        await _store.WriteTextAsync("settings.json", "old", Ct);
        await _store.WriteTextAsync("settings.json", "new", Ct);

        (await _store.ReadTextAsync("settings.json", Ct)).Should().Be("new");
        Directory.GetFiles(_store.DisplayRoot).Should().ContainSingle();
    }

    [Fact]
    public async Task FilesAreUtf8WithoutABom()
    {
        await _store.WriteTextAsync("a.txt", "x", Ct);

        File.ReadAllBytes(Path.Combine(_store.DisplayRoot, "a.txt")).Should().Equal((byte)'x');
    }

    [Fact]
    public async Task AMissingFileReadsAsNull()
    {
        (await _store.ReadTextAsync("nope/missing.json", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingInAMissingFolderIsFine()
    {
        // The first catalog-cache write deletes its marker before the folder exists.
        Func<Task> delete = () => _store.DeleteAsync("catalog-cache/fetched.txt", Ct);

        await delete.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AppendAddsToTheEnd()
    {
        await _store.AppendTextAsync("logs/run.log", "one\n", Ct);
        await _store.AppendTextAsync("logs/run.log", "two\n", Ct);

        (await _store.ReadTextAsync("logs/run.log", Ct)).Should().Be("one\ntwo\n");
    }

    [Fact]
    public async Task ListReturnsRelativeForwardSlashPaths()
    {
        await _store.WriteTextAsync("logs/b.log", "", Ct);
        await _store.WriteTextAsync("logs/a.log", "", Ct);

        (await _store.ListAsync("logs", Ct)).Should().Equal("logs/a.log", "logs/b.log");
        (await _store.ListAsync("nothing-here", Ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("carts/../../escape.txt")]
    [InlineData(@"C:\Windows\escape.txt")]
    [InlineData("D:escape.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("")]
    public async Task PathsCannotEscapeTheDataFolder(string path)
    {
        Func<Task> write = () => _store.WriteTextAsync(path, "x", Ct);

        await write.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task FailuresBecomeDataStoreExceptionsAndAreRecorded()
    {
        // A file where the data folder should be: every write fails, like a read-only drive.
        string blocked = Path.Combine(_temp.Path, "blocked");
        await File.WriteAllTextAsync(blocked, "I am a file", Ct);
        PortableDataStore store = new(blocked);
        string? reported = null;
        store.Failed += m => reported = m;

        Func<Task> write = () => store.WriteTextAsync("settings.json", "x", Ct);

        (await write.Should().ThrowAsync<DataStoreException>()).Which.Message.Should().Contain("save settings.json");
        store.LastFailure.Should().NotBeNull();
        reported.Should().Be(store.LastFailure);
        (await store.ProbeWritableAsync(Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task AWritableFolderProbesClean()
    {
        (await _store.ProbeWritableAsync(Ct)).Should().BeNull();
        Directory.GetFiles(_store.DisplayRoot).Should().BeEmpty();
    }
}
