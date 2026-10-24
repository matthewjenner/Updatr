using System.Runtime.CompilerServices;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Core.Tests.Preflight;

namespace Updatr.Core.Tests.Packages;

/// <summary>
/// The rules that decide what an upgrade touches. These are the safety-critical ones: a launcher's
/// game must never be changed behind the launcher's back, and "upgrade all" must respect holds.
/// </summary>
public sealed class PackageBatchTests
{
    private static readonly HoldPolicy Policy = new(
    [
        new() { Source = "fake", Pattern = "OpenJS.NodeJS*", Group = "Runtimes", Reason = "majors" },
        new() { Source = "fake", Pattern = "Microsoft.AppInstaller", Group = "Self-servicing", Reason = "store", Behavior = HoldBehavior.StoreManaged },
        new() { Source = "fake", Pattern = "Discord.Discord", Group = "Self-updating", Reason = "self", Behavior = HoldBehavior.Explicit },
    ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InstalledPackage Pkg(string id, string? available = "2.0", string installed = "1.0", string? managedBy = null, bool managed = true, string? fresh = null)
        => new()
        {
            Source = "fake",
            Id = id,
            Name = id,
            InstalledVersion = installed,
            AvailableVersion = available,
            ManagedBy = managedBy,
            IsManaged = managed,
            NewerVersionNeedsFreshInstall = fresh,
        };

    private static (PackageBatch Batch, FakeSource Source) Build(params InstalledPackage[] installed)
    {
        FakeSource source = new(installed);
        ListPackages list = new([source], _ => Task.FromResult(Policy));
        return (new PackageBatch([source], list, new IdleGate(), _ => Task.FromResult(Policy)), source);
    }

    [Fact]
    public async Task UpgradeAllSkipsHeldStoreSelfUpdatingLauncherAndUnknownVersions()
    {
        (PackageBatch batch, FakeSource source) = Build(
            Pkg("Git.Git"),
            Pkg("OpenJS.NodeJS.LTS"),
            Pkg("Microsoft.AppInstaller"),
            Pkg("Discord.Discord"),
            Pkg("Steam App 730", managedBy: "Steam"),
            Pkg("Blizzard.BattleNet", installed: "Unknown"),
            Pkg("Current.App", available: null));

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(), Ct);

        source.Upgraded.Should().Equal("Git.Git");
        summary.Items.Where(i => i.Outcome.Kind == OutcomeKind.Skipped).Select(i => i.DisplayName)
            .Should().BeEquivalentTo("OpenJS.NodeJS.LTS", "Microsoft.AppInstaller", "Discord.Discord", "Steam App 730", "Blizzard.BattleNet");
    }

    [Fact]
    public async Task IncludeHeldAndUnknownBringThemInButNeverLauncherOrStoreOrSelfUpdating()
    {
        (PackageBatch batch, FakeSource source) = Build(
            Pkg("OpenJS.NodeJS.LTS"),
            Pkg("Blizzard.BattleNet", installed: "Unknown"),
            Pkg("Microsoft.AppInstaller"),
            Pkg("Discord.Discord"),
            Pkg("Steam App 730", managedBy: "Steam"));

        await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true, IncludeHeld = true, IncludeUnknown = true }, new RecordingSink(), Ct);

        source.Upgraded.Should().BeEquivalentTo("OpenJS.NodeJS.LTS", "Blizzard.BattleNet");
    }

    [Fact]
    public async Task NamingAHeldOrSelfUpdatingPackageUpgradesIt()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("OpenJS.NodeJS.LTS"), Pkg("Discord.Discord"));

        await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { Ids = ["OpenJS.NodeJS.LTS", "Discord.Discord"] }, new RecordingSink(), Ct);

        source.Upgraded.Should().Equal("OpenJS.NodeJS.LTS", "Discord.Discord");
    }

    [Fact]
    public async Task ALauncherGameIsNeverTouchedEvenWhenNamed()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Steam App 730", managedBy: "Steam"));

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Reinstall, new PackageSelection { Ids = ["Steam App 730"] }, new RecordingSink(), Ct);

        source.Upgraded.Should().BeEmpty();
        source.Reinstalled.Should().BeEmpty();
        summary.Items.Single().Outcome.Detail.Should().Contain("Managed by Steam");
    }

    [Fact]
    public async Task ANewerVersionThatNeedsAFreshInstallIsExplainedNotAttempted()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Audacity.Audacity", available: null, fresh: "4.0.1"));

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { Ids = ["Audacity.Audacity"] }, new RecordingSink(), Ct);

        source.Upgraded.Should().BeEmpty();
        summary.Items.Single().Outcome.Detail.Should().Contain("4.0.1").And.Contain("fresh");
    }

    [Fact]
    public async Task UnknownIdsAreReportedNotIgnored()
    {
        (PackageBatch batch, _) = Build();

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { Ids = ["Nope.Nope"] }, new RecordingSink(), Ct);

        summary.Items.Single().Outcome.Detail.Should().Contain("Not installed");
    }

    [Fact]
    public async Task DecliningTheConfirmationChangesNothing()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Git.Git"));

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(answer: false), Ct);

        source.Upgraded.Should().BeEmpty();
        summary.Items.Single().Outcome.Detail.Should().Be("Not confirmed");
    }

    [Fact]
    public async Task ABusyInstallerIsRetriedOnce()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Git.Git"));
        source.Results.Enqueue(PackageOutcome.FromCode(1618));
        source.Results.Enqueue(PackageOutcome.Ok());

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(), Ct);

        source.Upgraded.Should().Equal("Git.Git", "Git.Git");
        summary.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Ok);
    }

    [Fact]
    public async Task CancelingStopsBeforeTheNextPackageNotDuringOne()
    {
        using CancellationTokenSource cts = new();
        (PackageBatch batch, FakeSource source) = Build(Pkg("A.A"), Pkg("B.B"), Pkg("C.C"));
        source.OnUpgrade = _ => cts.Cancel();

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(), cts.Token);

        source.Upgraded.Should().Equal("A.A");
        summary.Count(OutcomeKind.Ok).Should().Be(1);
        summary.Count(OutcomeKind.Skipped).Should().Be(2);
    }

    private sealed class FakeSource(InstalledPackage[] installed) : IPackageSource
    {
        public string Id => "fake";

        public List<string> Upgraded { get; } = [];

        public List<string> Reinstalled { get; } = [];

        public Queue<PackageOutcome> Results { get; } = new();

        public Action<InstalledPackage>? OnUpgrade { get; set; }

        public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (InstalledPackage p in installed)
            {
                await Task.Yield();
                yield return p;
            }
        }

        public Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(installed.FirstOrDefault(p => p.Id == id));

        public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            Upgraded.Add(package.Id);
            OnUpgrade?.Invoke(package);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : PackageOutcome.Ok());
        }

        public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            Reinstalled.Add(package.Id);
            return Task.FromResult(PackageOutcome.Ok());
        }
    }

    private sealed class IdleGate : IInstallerGate
    {
        public Task WaitUntilIdleAsync(IOperationSink sink, string itemId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
