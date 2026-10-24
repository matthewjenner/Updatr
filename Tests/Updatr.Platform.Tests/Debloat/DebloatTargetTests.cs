using Microsoft.Win32;
using Updatr.Core.Catalog;
using Updatr.Core.Debloat;
using Updatr.Core.Sources;
using Updatr.Platform.Debloat;
using Updatr.Platform.Elevation;
using Hive = Updatr.Core.Catalog.RegistryHive;

namespace Updatr.Platform.Tests.Debloat;

public sealed class DebloatTargetTests : IAsyncDisposable
{
    // A scratch key in the test user's own hive; removed after each test.
    private readonly string _key = $@"Software\Updatr.Tests\{Guid.NewGuid():N}";
    private readonly ElevationService _elevation = new("never-launched.exe", null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RegistryAction Action(string name, int? dword = null, bool delete = false)
        => new() { Hive = Hive.CurrentUser, Key = _key, Name = name, Dword = dword, Delete = delete };

    [Fact]
    public async Task ARegistryValueThatDidNotExistIsRemovedAgainOnRevert()
    {
        RegistryTarget target = new(_elevation);
        RegistryAction action = Action("Ads", dword: 0);

        (await target.DetectAsync(action, Ct)).State.Should().Be(ActionState.NotApplied);
        ActionBackup backup = await target.CaptureAsync(action, Ct);
        (await target.ApplyAsync(action, "t", new NullSink(), Ct)).Kind.Should().Be(OutcomeKind.Ok);
        (await target.DetectAsync(action, Ct)).State.Should().Be(ActionState.Applied);

        (await target.RevertAsync(backup, "t", new NullSink(), Ct)).Kind.Should().Be(OutcomeKind.Ok);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_key);
        key?.GetValue("Ads").Should().BeNull();
    }

    [Fact]
    public async Task APriorValueOfAnotherKindIsRestoredExactly()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_key))
            key.SetValue("Run", @"%LOCALAPPDATA%\app.exe /background", RegistryValueKind.ExpandString);

        RegistryTarget target = new(_elevation);
        RegistryAction action = Action("Run", delete: true);
        ActionBackup backup = await target.CaptureAsync(action, Ct);

        await target.ApplyAsync(action, "t", new NullSink(), Ct);
        (await target.DetectAsync(action, Ct)).State.Should().Be(ActionState.Applied);
        await target.RevertAsync(backup, "t", new NullSink(), Ct);

        using RegistryKey restored = Registry.CurrentUser.OpenSubKey(_key)!;
        restored.GetValueKind("Run").Should().Be(RegistryValueKind.ExpandString);
        restored.GetValue("Run", null, RegistryValueOptions.DoNotExpandEnvironmentNames).Should().Be(@"%LOCALAPPDATA%\app.exe /background");
    }

    [Fact]
    public void TheRegExportOfABackupRestoresTheValues()
    {
        BackupFile backup = new()
        {
            At = DateTimeOffset.UnixEpoch,
            Items = [new ItemBackup
            {
                ItemId = "x",
                Title = "X",
                Actions =
                [
                    new ActionBackup { Action = Action("Gone", dword: 0), Existed = false },
                    new ActionBackup { Action = Action("Was1", dword: 0), Existed = true, Value = "1", ValueKind = "DWord" },
                    new ActionBackup { Action = Action("Text", dword: 0), Existed = true, Value = @"C:\a ""b""", ValueKind = "String" },
                ],
            }],
        };

        string reg = RegExport.From(backup);

        reg.Should().StartWith("Windows Registry Editor Version 5.00");
        reg.Should().Contain($@"[HKEY_CURRENT_USER\{_key}]");
        reg.Should().Contain("\"Gone\"=-").And.Contain("\"Was1\"=dword:00000001").And.Contain(@"""Text""=""C:\\a \""b\""""");
    }

    [Theory]
    [InlineData(Hive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", true)]
    [InlineData(Hive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", true)]
    [InlineData(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip", false)]
    [InlineData(Hive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", false)]
    public void TheBrokerOnlyWritesRegistryKeysDebloatRulesMayUse(Hive hive, string key, bool allowed)
    {
        RegistryAction action = new() { Hive = hive, Key = key, Name = "X", Dword = 1 };

        Action act = () => DebloatBrokerHandler.Allowed(action);

        if (allowed)
            act.Should().NotThrow();
        else
            act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheBrokerRefusesTasksOutsideMicrosoftAndMalformedServices()
    {
        Action task = () => DebloatBrokerHandler.Allowed(new ScheduledTaskAction { Path = @"\Vendor\Updater" });
        Action service = () => DebloatBrokerHandler.Allowed(new ServiceAction { Name = "x & calc", StartType = ServiceStartType.Disabled });

        task.Should().Throw<ArgumentException>();
        service.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("junk")]
    public void ServiceRevertNeverRestoresDriverStartTypes(string value)
    {
        ServiceAction action = new() { Name = "DiagTrack", StartType = ServiceStartType.Disabled };

        PackageOutcome outcome = ServiceTarget.RevertLocal(action, new ActionBackup { Action = action, Existed = true, Value = value });

        outcome.Kind.Should().Be(OutcomeKind.Failed);
    }

    [Fact]
    public async Task AMissingScheduledTaskIsNotApplicable()
    {
        ScheduledTaskTarget target = new(_elevation);

        ActionStatus status = await target.DetectAsync(new ScheduledTaskAction { Path = @"\Microsoft\Windows\Updatr.Tests\DoesNotExist" }, Ct);

        status.State.Should().Be(ActionState.NotApplicable);
    }

    [Fact]
    public async Task AMissingServiceIsNotApplicable()
    {
        ServiceTarget target = new(_elevation);

        ActionStatus status = await target.DetectAsync(new ServiceAction { Name = "UpdatrTestsNoSuchService", StartType = ServiceStartType.Disabled }, Ct);

        status.State.Should().Be(ActionState.NotApplicable);
    }

    public async ValueTask DisposeAsync()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        await _elevation.DisposeAsync();
    }

    private sealed class NullSink : Core.Operations.IOperationSink
    {
        public void Report(Core.Operations.OperationEvent operationEvent)
        {
        }

        public Task<bool> ConfirmAsync(Core.Operations.Confirmation confirmation, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
