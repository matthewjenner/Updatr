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

    private static (PackageBatch Batch, FakeSource Source) Build(params InstalledPackage[] installed) => Build(null, installed);

    private static (PackageBatch Batch, FakeSource Source) Build(IInUseFinder? inUse, params InstalledPackage[] installed)
    {
        FakeSource source = new(installed);
        ListPackages list = new([source], _ => Task.FromResult(Policy));
        return (new PackageBatch([source], list, new IdleGate(), _ => Task.FromResult(Policy), verifyDelay: TimeSpan.Zero, inUse: inUse), source);
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
        summary.Items.Single().Outcome.Detail.Should().Contain("4.0.1").And.Contain("Fresh install");
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
    public async Task TheConfirmationListsEachPackageWithItsVersionNowAndAfter()
    {
        (PackageBatch batch, _) = Build(Pkg("CoreyButler.NVMforWindows", available: null, installed: "1.1.12", fresh: "1.2.2"));
        RecordingSink sink = new(answer: false);

        await new FreshInstallPackages(batch).RunAsync(new PackageSelection { Ids = ["CoreyButler.NVMforWindows"] }, sink, Ct);

        ConfirmationItem item = sink.Confirmations.Single().Items.Single();
        item.Id.Should().Be("CoreyButler.NVMforWindows");
        item.Current.Should().Be("1.1.12");
        item.Next.Should().Be("1.2.2");
    }

    [Fact]
    public void TheConsoleTableLeavesOutEmptyColumnsAndAlignsTheRest()
    {
        Confirmation confirmation = new("Upgrade 2 package(s)?", "")
        {
            Items = [new("Git", "Git.Git", "2.40", "2.51"), new("Visual Studio Code", "Microsoft.VisualStudioCode", "1.90", "1.105")],
        };

        string[] lines = ConfirmationColumns.Table(confirmation).Split('\n');

        lines[0].Should().Be("  Name                Id                          Installed  After");
        lines[2].Should().Be("  Git                 Git.Git                     2.40       2.51");
        lines.Should().HaveCount(4);
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

    [Fact]
    public async Task AnUpgradeThatLeavesTheOldVersionInPlaceIsPendingNotDone()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Microsoft.WindowsTerminal", available: "1.25"), Pkg("Git.Git"));
        source.Staged.Add("Microsoft.WindowsTerminal");

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(), Ct);

        summary.Items.Single(i => i.ItemId == "fake:Microsoft.WindowsTerminal").Outcome.Kind.Should().Be(OutcomeKind.Pending);
        summary.Items.Single(i => i.ItemId == "fake:Git.Git").Outcome.Kind.Should().Be(OutcomeKind.Ok);
        summary.HasFailures.Should().BeFalse();
    }

    [Fact]
    public async Task AnUpdateBlockedByARunningProgramNamesIt()
    {
        FakeInUse inUse = new([new BlockingProcess("ollama.exe", 7), new BlockingProcess("Code.exe", 8), new BlockingProcess("Code.exe", 9)]);
        (PackageBatch batch, FakeSource source) = Build(inUse, Pkg("Microsoft.WindowsTerminal"), Pkg("OBSProject.OBSStudio"), Pkg("Zed.Zed"));
        source.Results.Enqueue(PackageOutcome.Ok()); // Terminal: "OK", but staged
        source.Results.Enqueue(PackageOutcome.FromCode(0x8A150101)); // OBS: "in use"
        source.Staged.Add("Microsoft.WindowsTerminal");

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true }, new RecordingSink(), Ct);

        summary.Items.Single(i => i.ItemId == "fake:OBSProject.OBSStudio").Outcome.Detail.Should().EndWith("; in use by ollama.exe (pid 7), Code.exe (2 processes)");
        summary.Items.Single(i => i.ItemId == "fake:Microsoft.WindowsTerminal").Outcome.Detail.Should().Contain("in use by ollama.exe");
        summary.Items.Single(i => i.ItemId == "fake:Zed.Zed").Outcome.Detail.Should().BeEmpty();
        inUse.Asked.Should().BeEquivalentTo("OBSProject.OBSStudio", "Microsoft.WindowsTerminal");
    }

    [Fact]
    public async Task FreshInstallUninstallsThenInstallsTheNewestVersion()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("CoreyButler.NVMforWindows", available: null, installed: "1.1.12", fresh: "1.2.2"));

        RunSummary summary = await new FreshInstallPackages(batch).RunAsync(new PackageSelection { Ids = ["CoreyButler.NVMforWindows"] }, new RecordingSink(), Ct);

        source.Uninstalled.Should().Equal("CoreyButler.NVMforWindows");
        source.InstalledFresh.Should().Equal("CoreyButler.NVMforWindows");
        summary.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Ok);
        summary.Items.Single().Outcome.Detail.Should().Be("Installed fresh: 1.1.12 -> 1.2.2");
    }

    [Fact]
    public async Task AFailedUninstallInstallsNothing()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Some.App"));
        source.UninstallResult = PackageOutcome.Failed("uninstaller exit 1603");

        RunSummary summary = await new FreshInstallPackages(batch).RunAsync(new PackageSelection { Ids = ["Some.App"] }, new RecordingSink(), Ct);

        source.InstalledFresh.Should().BeEmpty();
        summary.Items.Single().Outcome.Detail.Should().StartWith("uninstall did not finish, so nothing was installed");
    }

    [Fact]
    public async Task AFailedInstallAfterTheUninstallSaysTheAppIsGone()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Some.App"));
        source.InstallResult = PackageOutcome.Failed("download failed");

        RunSummary summary = await new FreshInstallPackages(batch).RunAsync(new PackageSelection { Ids = ["Some.App"] }, new RecordingSink(), Ct);

        summary.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Failed);
        summary.Items.Single().Outcome.Detail.Should().Contain("was uninstalled, but installing the new version failed");
    }

    [Fact]
    public async Task FreshInstallNeverTouchesLauncherGamesOrWingetItself()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Steam App 730", managedBy: "Steam"), Pkg("Microsoft.AppInstaller"), Pkg("Unknown.App", managed: false));

        await new FreshInstallPackages(batch).RunAsync(new PackageSelection { Ids = ["Steam App 730", "Microsoft.AppInstaller", "Unknown.App"] }, new RecordingSink(), Ct);

        source.Uninstalled.Should().BeEmpty();
    }

    [Fact]
    public void ManyBlockersStayOnOneLine()
    {
        BlockingProcess[] many = [new("a.exe", 1), new("b.exe", 2, "no window"), new("c.exe", 3), new("d.exe", 4), new("e.exe", 5)];

        BlockingProcess.Describe(many).Should().Be("a.exe (pid 1), b.exe (pid 2; no window), c.exe (pid 3) and 2 more");
    }

    [Fact]
    public async Task UpgradeAllWithinAViewTouchesOnlyThatViewAndStillHonorsHolds()
    {
        InstalledPackage git = Pkg("Git.Git"), node = Pkg("OpenJS.NodeJS.LTS"), other = Pkg("Other.App");
        (PackageBatch batch, FakeSource source) = Build(git, node, other);
        ListedPackage[] shown = [new(git, null), new(node, Policy.Match("fake", node.Id))];

        await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true, Within = shown }, new RecordingSink(), Ct);

        // Other.App has an update but is not on screen; Node is on screen but held.
        source.Upgraded.Should().Equal("Git.Git");
    }

    [Fact]
    public async Task UpgradeAllCanBeNarrowedByKind()
    {
        (PackageBatch batch, FakeSource source) = Build(Pkg("Git.Git"));

        // FakeSource is not winget, so its packages are developer packages.
        await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true, Kinds = [AppKind.Game] }, new RecordingSink(), Ct);
        source.Upgraded.Should().BeEmpty();

        await batch.RunAsync(PackageBatch.Action.Upgrade, new PackageSelection { All = true, Kinds = [AppKind.Developer] }, new RecordingSink(), Ct);
        source.Upgraded.Should().Equal("Git.Git");
    }

    [Fact]
    public async Task UninstallTakesAppsNoCatalogKnowsButNeverLauncherGamesOrWinget()
    {
        (PackageBatch batch, FakeSource source) = Build(
            Pkg(@"ARP\Machine\X64\SomeTool", available: null, managed: false),
            Pkg("Steam App 730", managedBy: "Steam"),
            Pkg("Microsoft.AppInstaller"));

        RunSummary summary = await batch.RunAsync(PackageBatch.Action.Uninstall,
            new PackageSelection { Ids = [@"ARP\Machine\X64\SomeTool", "Steam App 730", "Microsoft.AppInstaller"] }, new RecordingSink(), Ct);

        source.Uninstalled.Should().Equal(@"ARP\Machine\X64\SomeTool");
        summary.Items.Where(i => i.Outcome.Kind == OutcomeKind.Skipped).Select(i => i.DisplayName).Should().BeEquivalentTo("Steam App 730", "Microsoft.AppInstaller");
    }

    [Fact]
    public async Task UninstallAllIsRefused()
    {
        (PackageBatch batch, _) = Build(Pkg("Git.Git"));

        Func<Task> act = () => new UninstallPackages(batch).RunAsync(new PackageSelection { All = true }, new RecordingSink(), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeInUse(IReadOnlyList<BlockingProcess> found) : IInUseFinder
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<BlockingProcess>> FindAsync(InstalledPackage package, CancellationToken cancellationToken)
        {
            Asked.Add(package.Id);
            return Task.FromResult(found);
        }
    }

    internal sealed class FakeSource(InstalledPackage[] installed) : IPackageSource
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

        /// <summary>Ids whose upgrade "succeeds" but leaves the old version in place (staged until restart).</summary>
        public HashSet<string> Staged { get; } = [];

        private readonly Dictionary<string, string> _versions = installed.ToDictionary(p => p.Id, p => p.InstalledVersion);

        /// <summary>Uninstalled and not installed again.</summary>
        private readonly HashSet<string> _gone = [];

        public Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken)
            => Task.FromResult(!_gone.Contains(id) && installed.FirstOrDefault(p => p.Id == id) is { } p ? p with { InstalledVersion = _versions[id] } : null);

        public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            Upgraded.Add(package.Id);

            if (!Staged.Contains(package.Id) && package.AvailableVersion is { } target)
                _versions[package.Id] = target;

            OnUpgrade?.Invoke(package);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : PackageOutcome.Ok());
        }

        public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            Reinstalled.Add(package.Id);
            return Task.FromResult(PackageOutcome.Ok());
        }

        public List<string> Uninstalled { get; } = [];

        public PackageOutcome UninstallResult { get; set; } = PackageOutcome.Ok();

        public Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            Uninstalled.Add(package.Id);

            if (UninstallResult.Kind == OutcomeKind.Ok)
                _gone.Add(package.Id);

            return Task.FromResult(UninstallResult);
        }

        public bool CanFreshInstall => true;

        public List<string> InstalledFresh { get; } = [];

        public PackageOutcome InstallResult { get; set; } = PackageOutcome.Ok();

        public Task<PackageOutcome> InstallLatestAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken)
        {
            InstalledFresh.Add(package.Id);

            if (InstallResult.Kind == OutcomeKind.Ok)
            {
                _gone.Remove(package.Id);
                _versions[package.Id] = package.NewerVersionNeedsFreshInstall ?? package.AvailableVersion ?? package.InstalledVersion;
            }

            return Task.FromResult(InstallResult);
        }
    }

    internal sealed class IdleGate : IInstallerGate
    {
        public Task WaitUntilIdleAsync(IOperationSink sink, string itemId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
