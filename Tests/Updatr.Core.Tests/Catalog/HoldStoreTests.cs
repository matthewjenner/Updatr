using Updatr.Core.Abstractions;
using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

public sealed class HoldStoreTests
{
    private static readonly IReadOnlyList<HoldRule> Catalog =
    [
        new() { Pattern = "OpenJS.NodeJS*", Group = "Runtimes", Reason = "majors" },
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void HoldingAddsAnExactRuleAndClearsAReleaseOfTheSameId()
    {
        UserHoldsFile file = new() { Releases = [new HoldRelease { Pattern = "Git.Git" }] };

        UserHoldsFile held = HoldStore.Hold(file, "winget", "Git.Git", "pinned for a project");

        held.Releases.Should().BeEmpty();
        HoldStore.PolicyFor(Catalog, held).Match("winget", "Git.Git")!.IsUserRule.Should().BeTrue();
    }

    [Fact]
    public void HoldingTwiceKeepsOneRule()
    {
        UserHoldsFile file = HoldStore.Hold(HoldStore.Hold(new UserHoldsFile(), "winget", "Git.Git", "a"), "winget", "git.git", "b");

        file.Holds.Should().ContainSingle().Which.Reason.Should().Be("b");
    }

    [Fact]
    public void UnholdingACatalogHoldRecordsARelease()
    {
        UserHoldsFile file = HoldStore.Unhold(new UserHoldsFile(), Catalog, "winget", "OpenJS.NodeJS.LTS");

        file.Releases.Should().ContainSingle().Which.Pattern.Should().Be("OpenJS.NodeJS.LTS");
        HoldStore.PolicyFor(Catalog, file).Match("winget", "OpenJS.NodeJS.LTS").Should().BeNull();
        HoldStore.PolicyFor(Catalog, file).Match("winget", "OpenJS.NodeJS").Should().NotBeNull("only the named id was released");
    }

    [Fact]
    public void UnholdingYourOwnHoldJustRemovesIt()
    {
        UserHoldsFile file = HoldStore.Unhold(HoldStore.Hold(new UserHoldsFile(), "winget", "Git.Git", "a"), Catalog, "winget", "Git.Git");

        file.Holds.Should().BeEmpty();
        file.Releases.Should().BeEmpty("nothing in the catalog holds Git.Git here");
    }

    [Fact]
    public async Task HoldsRoundTripThroughTheDataStore()
    {
        MemoryDataStore data = new();
        HoldStore store = new(data);

        await store.SaveAsync(HoldStore.Hold(new UserHoldsFile(), "winget", "Git.Git", "a"), Ct);
        UserHoldsFile loaded = await store.LoadAsync(Ct);

        loaded.Holds.Should().ContainSingle().Which.Pattern.Should().Be("Git.Git");
        data.Files[HoldStore.FileName].Should().Contain("\"pattern\": \"Git.Git\"");
    }

    [Fact]
    public async Task ABrokenHoldsFileFallsBackToNoUserHolds()
    {
        MemoryDataStore data = new();
        data.Files[HoldStore.FileName] = "{ this is not json";

        (await new HoldStore(data).LoadAsync(Ct)).Holds.Should().BeEmpty();
    }
}

/// <summary>An in-memory <see cref="IDataStore"/>.</summary>
internal sealed class MemoryDataStore : IDataStore
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayRoot => "memory";

    public string? LastFailure => null;

    public event Action<string>? Failed;

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken)
        => Task.FromResult(Files.GetValueOrDefault(relativePath));

    public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        Files[relativePath] = content;
        return Task.CompletedTask;
    }

    public Task AppendTextAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        Files[relativePath] = Files.GetValueOrDefault(relativePath) + content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        Files.Remove(relativePath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string relativeFolder, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>([.. Files.Keys.Where(k => k.StartsWith(relativeFolder + "/", StringComparison.OrdinalIgnoreCase))]);

    public Task<string?> ProbeWritableAsync(CancellationToken cancellationToken)
    {
        Failed?.Invoke("never");
        return Task.FromResult<string?>(null);
    }
}
