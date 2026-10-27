using Updatr.Core.Carts;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Core.Tests.Catalog;
using Updatr.Core.Tests.Preflight;

namespace Updatr.Core.Tests.Carts;

public sealed class InstallAppsTests
{
    private static readonly AppCatalog Catalog = new(
        [
            new() { Id = "nodejs-lts", Name = "Node", Category = "dev", Sources = [new WingetSourceRef { Id = "OpenJS.NodeJS.LTS" }], Provides = ["npm"] },
            new() { Id = "typescript", Name = "TypeScript", Category = "dev", Sources = [new NpmSourceRef { Package = "typescript" }] },
            new() { Id = "git", Name = "Git", Category = "dev", Sources = [new WingetSourceRef { Id = "Git.Git" }] },
        ],
        [],
        []);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (InstallApps Op, FakeInstaller Installer, FakeTools Tools) Build()
    {
        FakeInstaller installer = new();
        FakeTools tools = new();
        CartStore store = new(new MemoryDataStore());
        TemplateCatalog templates = new(_ => Task.FromResult(Catalog), store);
        return (new InstallApps(_ => Task.FromResult(Catalog), templates, store, [installer], new IdleGate(), tools), installer, tools);
    }

    [Fact]
    public async Task PrerequisitesInstallFirstAndPathIsRefreshedAfterAProvider()
    {
        (InstallApps op, FakeInstaller installer, FakeTools tools) = Build();

        RunSummary summary = await op.RunAsync(new InstallRequest { AppIds = ["typescript"] }, new RecordingSink(), Ct);

        installer.Installed.Should().Equal("OpenJS.NodeJS.LTS", "typescript");
        tools.Refreshes.Should().Be(1, "after Node, so npm is found for TypeScript");
        summary.Count(OutcomeKind.Ok).Should().Be(2);
    }

    [Fact]
    public async Task AlreadyInstalledAppsAreSkippedAsCurrent()
    {
        (InstallApps op, FakeInstaller installer, _) = Build();
        installer.AlreadyThere.Add("Git.Git");

        RunSummary summary = await op.RunAsync(new InstallRequest { AppIds = ["git"] }, new RecordingSink(), Ct);

        installer.Installed.Should().BeEmpty();
        summary.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.AlreadyCurrent);
    }

    [Fact]
    public async Task AFailedPrerequisiteSkipsWhatNeedsItWithAClearReason()
    {
        (InstallApps op, FakeInstaller installer, _) = Build();
        installer.Fails.Add("OpenJS.NodeJS.LTS");

        RunSummary summary = await op.RunAsync(new InstallRequest { AppIds = ["typescript", "git"] }, new RecordingSink(), Ct);

        installer.Installed.Should().Equal("OpenJS.NodeJS.LTS", "Git.Git");
        summary.Items.Single(i => i.ItemId == "typescript").Outcome.Detail.Should().Be("A prerequisite failed to install");
    }

    [Fact]
    public async Task TheConfirmationListsWhyPrerequisitesAreThere()
    {
        (InstallApps op, _, _) = Build();
        RecordingSink sink = new(answer: false);

        await op.RunAsync(new InstallRequest { AppIds = ["typescript"] }, sink, Ct);

        sink.Confirmations.Single().Items.Should().ContainEquivalentOf(new ConfirmationItem("Node", "winget", Note: "Required by TypeScript"));
    }

    private sealed class FakeInstaller : IAppInstaller
    {
        public List<string> Installed { get; } = [];

        public HashSet<string> AlreadyThere { get; } = [];

        public HashSet<string> Fails { get; } = [];

        public bool Handles(PackageSourceRef source) => true;

        public Task<bool> IsInstalledAsync(PackageSourceRef source, CancellationToken cancellationToken) => Task.FromResult(AlreadyThere.Contains(Key(source)));

        public Task<PackageOutcome> InstallAsync(PackageSourceRef source, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        {
            Installed.Add(Key(source));
            return Task.FromResult(Fails.Contains(Key(source)) ? PackageOutcome.Failed("boom") : PackageOutcome.Ok());
        }

        private static string Key(PackageSourceRef source) => source switch
        {
            WingetSourceRef w => w.Id,
            NpmSourceRef n => n.Package,
            _ => "?",
        };
    }

    private sealed class FakeTools : IToolEnvironment
    {
        public int Refreshes { get; private set; }

        public IReadOnlySet<string> ToolsPresent() => new HashSet<string>();

        public void Refresh() => Refreshes++;
    }

    private sealed class IdleGate : IInstallerGate
    {
        public Task WaitUntilIdleAsync(IOperationSink sink, string itemId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
