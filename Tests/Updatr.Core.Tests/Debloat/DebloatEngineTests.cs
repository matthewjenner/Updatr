using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Sources;
using Updatr.Core.Tests.Catalog;
using Updatr.Core.Tests.Preflight;

namespace Updatr.Core.Tests.Debloat;

public sealed class DebloatEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegistryAction Value(string name, bool machine = false) => new()
    {
        Hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        Key = machine ? @"SOFTWARE\Test" : @"Software\Test",
        Name = name,
        Dword = 0,
    };

    private static DebloatItem Item(string id, DebloatGroup group, params DebloatAction[] actions)
        => new() { Id = id, Group = group, Category = "Test", Title = id, Description = id, Actions = actions };

    private static AppCatalog Catalog(params DebloatItem[] items)
        => new([], [], [], new DebloatFile { SchemaVersion = 1, Items = items, Keep = ["Microsoft.WindowsCalculator"] });

    private sealed record Rig(DebloatEngine Engine, FakeTarget Target, FakeInventory Inventory, FakeRestorePoint RestorePoint, MemoryDataStore Data);

    private static Rig Build(AppCatalog catalog, int osBuild = 26100)
    {
        MemoryDataStore data = new();
        FakeTarget target = new(data);
        FakeInventory inventory = new();
        FakeRestorePoint restorePoint = new();
        DebloatEngine engine = new(_ => Task.FromResult(catalog), [target], inventory, restorePoint, new DebloatStore(data), osBuild);
        return new Rig(engine, target, inventory, restorePoint, data);
    }

    [Fact]
    public async Task OnlyWhatHasDriftedIsChanged()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Recommended, Value("A1"), Value("A2")), Item("b", DebloatGroup.Recommended, Value("B1"))));
        rig.Target.Applied.Add("A1");
        rig.Target.Applied.Add("B1");

        RunSummary summary = await rig.Engine.ApplyAsync(new DebloatApplyRequest { Recommended = true }, new RecordingSink(), Ct);

        rig.Target.Calls.Should().Equal("apply A2");
        summary.Items.Single(i => i.ItemId == "a").Outcome.Kind.Should().Be(OutcomeKind.Ok);
        summary.Items.Single(i => i.ItemId == "b").Outcome.Kind.Should().Be(OutcomeKind.AlreadyCurrent);
    }

    [Fact]
    public async Task TheBackupIsSavedBeforeAnythingChanges()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1"))));

        await rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a"] }, new RecordingSink(), Ct);

        rig.Target.BackupSeenAtApply.Should().Equal(true);
        rig.Data.Files.Keys.Should().Contain(k => k.StartsWith("debloat/backups/", StringComparison.Ordinal) && k.EndsWith(".reg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoBackupMeansNoChange()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1")), Item("b", DebloatGroup.Optional, Value("B1"))));
        rig.Data.ReadOnly = true;

        RunSummary summary = await rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a", "b"] }, new RecordingSink(), Ct);

        rig.Target.Calls.Should().BeEmpty();
        summary.Items.Select(i => i.Outcome.Kind).Should().Equal(OutcomeKind.Failed, OutcomeKind.Skipped);
    }

    [Fact]
    public async Task DecliningTheConfirmationChangesNothing()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Recommended, Value("A1", machine: true))));

        RunSummary summary = await rig.Engine.ApplyAsync(new DebloatApplyRequest { Recommended = true }, new RecordingSink(answer: false), Ct);

        rig.Target.Calls.Should().BeEmpty();
        rig.RestorePoint.Created.Should().Be(0);
        summary.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Skipped);
    }

    [Fact]
    public async Task ARestorePointIsMadeOnlyForMachineWideChanges()
    {
        Rig user = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1"))));
        await user.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a"] }, new RecordingSink(), Ct);

        Rig machine = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1", machine: true))));
        await machine.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a"] }, new RecordingSink(), Ct);

        user.RestorePoint.Created.Should().Be(0);
        machine.RestorePoint.Created.Should().Be(1);
    }

    [Fact]
    public async Task RevertUndoesTheLatestApplyInReverseOrderAndOnlyOnce()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1"), Value("A2"))));
        await rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a"] }, new RecordingSink(), Ct);
        rig.Target.Calls.Clear();

        RunSummary first = await rig.Engine.RevertAsync(["a"], new RecordingSink(), Ct);
        RunSummary second = await rig.Engine.RevertAsync(["a"], new RecordingSink(), Ct);

        rig.Target.Calls.Should().Equal("revert A2", "revert A1");
        first.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Ok);
        second.Items.Single().Outcome.Should().Be(new PackageOutcome(OutcomeKind.AlreadyCurrent, "Already reverted"));
    }

    [Fact]
    public async Task ItemsPutBackAfterARunAreFlaggedAsDrifted()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Recommended, Value("A1")), Item("b", DebloatGroup.Recommended, Value("B1"))));
        await rig.Engine.ApplyAsync(new DebloatApplyRequest { Recommended = true }, new RecordingSink(), Ct);

        rig.Target.Applied.Remove("A1"); // A feature update put it back.
        DebloatReport report = await rig.Engine.StatusAsync(Ct);

        report.Items.Single(i => i.Item.Id == "a").Drifted.Should().BeTrue();
        report.Items.Single(i => i.Item.Id == "b").Drifted.Should().BeFalse();
        report.LastRun.Should().NotBeNull();
    }

    [Fact]
    public async Task UnrecognizedAppsExcludeRuleTargetsAndBothKeepLists()
    {
        Rig rig = Build(Catalog(Item("junk", DebloatGroup.Recommended, new AppxAction { Name = "king.com.*" })));
        rig.Inventory.Apps.AddRange([
            new StoreApp("king.com.CandyCrushSaga", "Candy Crush", null),
            new StoreApp("Microsoft.WindowsCalculator", "Calculator", null),
            new StoreApp("Vendor.Tool", "Vendor Tool", null),
            new StoreApp("Other.App", "Other", null),
        ]);
        await new KeepApps(rig.Engine.Store).RunAsync(["Vendor.*"], new RecordingSink(), Ct);

        DebloatReport report = await rig.Engine.StatusAsync(Ct);

        report.Unrecognized.Select(u => (u.App.Name, u.Kept)).Should().Equal(("Other.App", false), ("Vendor.Tool", true));
    }

    [Fact]
    public async Task AppsInstalledSinceTheLastRunAreNew()
    {
        Rig rig = Build(Catalog(Item("a", DebloatGroup.Optional, Value("A1"))));
        rig.Inventory.Apps.Add(new StoreApp("Old.App", "Old", null));
        await rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["a"] }, new RecordingSink(), Ct);

        rig.Inventory.Apps.Add(new StoreApp("New.App", "New", null));
        DebloatReport report = await rig.Engine.StatusAsync(Ct);

        report.Unrecognized.Single(u => u.App.Name == "New.App").IsNew.Should().BeTrue();
        report.Unrecognized.Single(u => u.App.Name == "Old.App").IsNew.Should().BeFalse();
    }

    [Fact]
    public async Task AnUnrecognizedAppCanBeRemovedAndReverted()
    {
        Rig rig = Build(Catalog());
        rig.Inventory.Apps.Add(new StoreApp("Other.App", "Other", null));

        RunSummary applied = await rig.Engine.ApplyAsync(new DebloatApplyRequest { RemoveApps = ["other.app"] }, new RecordingSink(), Ct);
        RunSummary reverted = await rig.Engine.RevertAsync(["app:Other.App"], new RecordingSink(), Ct);

        rig.Target.Calls.Should().Equal("apply remove app Other.App", "revert remove app Other.App");
        applied.Items.Single().ItemId.Should().Be("app:Other.App");
        reverted.Items.Single().Outcome.Kind.Should().Be(OutcomeKind.Ok);
    }

    [Fact]
    public async Task ItemsForNewerWindowsAreUnavailable()
    {
        DebloatItem recall = Item("recall", DebloatGroup.Optional, Value("R")) with { MinBuild = 26100 };
        Rig rig = Build(Catalog(recall), osBuild: 19045);

        RunSummary summary = await rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["recall"] }, new RecordingSink(), Ct);

        (await rig.Engine.StatusAsync(Ct)).Items.Single().State.Should().Be(ItemState.Unavailable);
        summary.Items.Single().Outcome.Should().Be(PackageOutcome.Skipped("Not on this version of Windows"));
        rig.Target.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownItemIdsAreAUsageError()
    {
        Rig rig = Build(Catalog());

        Func<Task> act = () => rig.Engine.ApplyAsync(new DebloatApplyRequest { ItemIds = ["nope"] }, new RecordingSink(), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(new[] { ActionState.Applied, ActionState.NotApplicable }, ItemState.Applied)]
    [InlineData(new[] { ActionState.NotApplicable }, ItemState.Applied)]
    [InlineData(new[] { ActionState.Applied, ActionState.NotApplied }, ItemState.Partial)]
    [InlineData(new[] { ActionState.NotApplied, ActionState.Unknown }, ItemState.NotApplied)]
    [InlineData(new[] { ActionState.Applied, ActionState.Unknown }, ItemState.Unknown)]
    public void ItemStateRollsUpFromItsActions(ActionState[] actions, ItemState expected)
        => DebloatEngine.RollUp([.. actions.Select(a => new ActionStatus(a))]).Should().Be(expected);

    [Fact]
    public void PartlyAppliedIsOkWithTheReason()
    {
        PackageOutcome outcome = DebloatEngine.Combine([PackageOutcome.Ok(), PackageOutcome.Skipped("needs admin")]);

        outcome.Should().Be(new PackageOutcome(OutcomeKind.Ok, "Partly applied - needs admin"));
    }

    /// <summary>Registry and appx actions against an in-memory "machine"; records calls in order.</summary>
    private sealed class FakeTarget(MemoryDataStore data) : IDebloatTarget
    {
        public HashSet<string> Applied { get; } = [];

        public List<string> Calls { get; } = [];

        public List<bool> BackupSeenAtApply { get; } = [];

        public bool Handles(DebloatAction action) => action is RegistryAction or AppxAction;

        public bool NeedsAdmin(DebloatAction action) => action is RegistryAction { Hive: RegistryHive.LocalMachine };

        private static string Key(DebloatAction action) => action is RegistryAction r ? r.Name : action.Describe;

        public Task<ActionStatus> DetectAsync(DebloatAction action, CancellationToken cancellationToken)
            => Task.FromResult(new ActionStatus(Applied.Contains(Key(action)) ? ActionState.Applied : ActionState.NotApplied));

        public Task<ActionBackup> CaptureAsync(DebloatAction action, CancellationToken cancellationToken)
            => Task.FromResult(new ActionBackup { Action = action, Existed = false });

        public Task<PackageOutcome> ApplyAsync(DebloatAction action, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        {
            BackupSeenAtApply.Add(data.Files.Any(f => f.Key.StartsWith("debloat/backups/", StringComparison.Ordinal) && f.Value.Contains(itemId, StringComparison.Ordinal)));
            Calls.Add("apply " + Key(action));
            Applied.Add(Key(action));
            return Task.FromResult(PackageOutcome.Ok());
        }

        public Task<PackageOutcome> RevertAsync(ActionBackup backup, string itemId, IOperationSink sink, CancellationToken cancellationToken)
        {
            Calls.Add("revert " + Key(backup.Action));
            Applied.Remove(Key(backup.Action));
            return Task.FromResult(PackageOutcome.Ok());
        }
    }

    private sealed class FakeInventory : IAppxInventory
    {
        public List<StoreApp> Apps { get; } = [];

        public Task<IReadOnlyList<StoreApp>> ListRemovableAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoreApp>>([.. Apps]);
    }

    private sealed class FakeRestorePoint : IRestorePoint
    {
        public int Created { get; private set; }

        public Task<string?> CreateAsync(string description, CancellationToken cancellationToken)
        {
            Created++;
            return Task.FromResult<string?>(null);
        }
    }
}
