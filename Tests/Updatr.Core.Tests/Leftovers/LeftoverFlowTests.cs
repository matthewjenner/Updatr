using Updatr.Core.Leftovers;
using Updatr.Core.Operations;
using Updatr.Core.Sources;
using Updatr.Core.Tests.Catalog;
using Updatr.Core.Tests.Preflight;

namespace Updatr.Core.Tests.Leftovers;

public sealed class LeftoverFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly InstalledPackage Package = new() { Source = "winget", Id = "Vendor.App", Name = "App", InstalledVersion = "1.0" };

    private static Leftover Item(string id, Confidence confidence = Confidence.Medium) => new(id, LeftoverKind.Folder, $@"C:\Data\App{id}", confidence, "test", 100);

    [Fact]
    public async Task TheAppIsDescribedBeforeAndScannedAfterASuccessfulUninstall()
    {
        FakeScanner scanner = new([Item("1"), Item("2")]);
        LeftoverStore store = new(new MemoryDataStore(), "pc");
        LeftoverService service = new(scanner, store);

        LeftoverTarget target = (await service.DescribeAsync(Package, Ct))!;
        int found = await service.ScanAsync(target, new RecordingSink(), "winget:Vendor.App", Ct);

        found.Should().Be(2);
        scanner.Described.Should().Equal("Vendor.App");
        (await store.ListAsync(Ct)).Single().Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task AScannerFailureNeverFailsTheUninstall()
    {
        LeftoverService service = new(new FakeScanner([]) { Throw = true }, new LeftoverStore(new MemoryDataStore(), "pc"));

        (await service.DescribeAsync(Package, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task QuarantinedItemsLeaveTheReviewAndFailedOnesStay()
    {
        LeftoverStore store = new(new MemoryDataStore(), "pc");
        await store.SaveAsync(new LeftoverScan { Id = "scan", At = DateTimeOffset.Now, Target = new() { Package = Package.Key, Name = "App" }, Items = [Item("1"), Item("2"), Item("3")] }, Ct);
        FakeQuarantine quarantine = new() { FailLocation = @"C:\Data\App2" };

        await new QuarantineLeftovers(store, quarantine).RunAsync(new QuarantineRequest("scan", ["1", "2"]), new RecordingSink(), Ct);

        quarantine.Moved.Should().Equal(@"C:\Data\App1", @"C:\Data\App2");
        (await store.GetAsync("scan", Ct)).Items.Select(i => i.Id).Should().Equal("2", "3");
    }

    [Fact]
    public async Task NothingIsQuarantinedUnlessPicked()
    {
        LeftoverStore store = new(new MemoryDataStore(), "pc");
        await store.SaveAsync(new LeftoverScan { Id = "scan", At = DateTimeOffset.Now, Target = new() { Package = Package.Key, Name = "App" }, Items = [Item("1", Confidence.Low)] }, Ct);

        Func<Task> act = () => new QuarantineLeftovers(store, new FakeQuarantine()).RunAsync(new QuarantineRequest("scan", [], AllHigh: true), new RecordingSink(), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ItemsThatHaveGoneSinceTheScanAreDropped()
    {
        LeftoverStore store = new(new MemoryDataStore(), "pc");
        await store.SaveAsync(new LeftoverScan { Id = "a", At = DateTimeOffset.Now, Target = new() { Package = Package.Key, Name = "A" }, Items = [Item("1"), Item("2")] }, Ct);
        await store.SaveAsync(new LeftoverScan { Id = "b", At = DateTimeOffset.Now, Target = new() { Package = Package.Key, Name = "B" }, Items = [Item("3")] }, Ct);
        FakeScanner scanner = new([]);
        scanner.Gone.UnionWith(["1", "3"]); // An uninstaller that finished after the scan.

        IReadOnlyList<LeftoverScan> listed = await new ListLeftovers(store, scanner).RunAsync(true, new RecordingSink(), Ct);

        listed.Should().ContainSingle().Which.Items.Select(i => i.Id).Should().Equal("2");
        (await store.ListAsync(Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task ScansAreKeptPerMachine()
    {
        MemoryDataStore data = new();
        await new LeftoverStore(data, "pc-a").SaveAsync(new LeftoverScan { Id = "scan", At = DateTimeOffset.Now, Target = new() { Package = Package.Key, Name = "App" }, Items = [Item("1")] }, Ct);

        (await new LeftoverStore(data, "pc-b").ListAsync(Ct)).Should().BeEmpty();
    }

    private sealed class FakeScanner(IReadOnlyList<Leftover> items) : ILeftoverScanner
    {
        public bool Throw { get; init; }

        public List<string> Described { get; } = [];

        public Task<LeftoverTarget?> DescribeAsync(InstalledPackage package, CancellationToken cancellationToken)
        {
            if (Throw)
                throw new InvalidOperationException("boom");

            Described.Add(package.Id);
            return Task.FromResult<LeftoverTarget?>(new LeftoverTarget { Package = package.Key, Name = package.Name });
        }

        public Task<IReadOnlyList<Leftover>> ScanAsync(LeftoverTarget target, CancellationToken cancellationToken) => Task.FromResult(items);

        public HashSet<string> Gone { get; } = [];

        public Task<IReadOnlyList<Leftover>> StillPresentAsync(IReadOnlyList<Leftover> present, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Leftover>>([.. present.Where(i => !Gone.Contains(i.Id))]);
    }

    private sealed class FakeQuarantine : IQuarantine
    {
        public string? FailLocation { get; init; }

        public List<string> Moved { get; } = [];

        public Task<QuarantineEntry> MoveAsync(string app, IReadOnlyList<Leftover> items, IOperationSink sink, CancellationToken cancellationToken)
        {
            Moved.AddRange(items.Select(i => i.Location));
            return Task.FromResult(new QuarantineEntry("entry", DateTimeOffset.Now, app,
                [.. items.Select(i => new QuarantinedItem(i.Kind, i.Location, i.Location != FailLocation))], 0));
        }

        public Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantineEntry>>([]);

        public Task<PackageOutcome> RestoreAsync(string entryId, IOperationSink sink, CancellationToken cancellationToken) => Task.FromResult(PackageOutcome.Ok());

        public Task PurgeAsync(string entryId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
