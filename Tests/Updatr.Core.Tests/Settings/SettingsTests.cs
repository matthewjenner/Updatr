using System.Runtime.CompilerServices;
using Updatr.Core.Catalog;
using Updatr.Core.Operations;
using Updatr.Core.Packages;
using Updatr.Core.Settings;
using Updatr.Core.Sources;
using Updatr.Core.Tests.Catalog;
using Updatr.Core.Tests.Preflight;

namespace Updatr.Core.Tests.Settings;

public sealed class SettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoFileMeansTheDefaultsNotZero()
    {
        UserSettings settings = await new SettingsStore(new MemoryDataStore()).LoadAsync(Ct);

        settings.UpdatrCheckEvery.Should().Be(UserSettings.DefaultUpdatrCheckHours);
        settings.AppCheckEvery.Should().Be(UserSettings.DefaultAppCheckHours);
    }

    [Fact]
    public async Task AFieldMissingFromTheFileIsItsDefault()
    {
        // The source-generated deserializer would make an omitted int 0 - "off".
        MemoryDataStore data = new();
        await data.WriteTextAsync(SettingsStore.FileName, """{ "appCheckHours": 0 }""", Ct);

        UserSettings settings = await new SettingsStore(data).LoadAsync(Ct);

        settings.AppCheckEvery.Should().Be(0);
        settings.UpdatrCheckEvery.Should().Be(UserSettings.DefaultUpdatrCheckHours);
    }

    [Fact]
    public async Task ABrokenFileFallsBackToTheDefaults()
    {
        MemoryDataStore data = new();
        await data.WriteTextAsync(SettingsStore.FileName, "{ not json", Ct);

        (await new SettingsStore(data).LoadAsync(Ct)).AppCheckEvery.Should().Be(UserSettings.DefaultAppCheckHours);
    }

    [Fact]
    public async Task AChangeKeepsTheOtherSettingAndIsSaved()
    {
        SettingsStore store = new(new MemoryDataStore());
        ChangeSettings change = new(store);

        await change.RunAsync(new SettingsChange(AppCheckHours: 4), new RecordingSink(), Ct);
        await change.RunAsync(new SettingsChange(UpdatrCheckHours: 0), new RecordingSink(), Ct);

        UserSettings saved = await new ShowSettings(store).RunAsync(true, new RecordingSink(), Ct);
        saved.AppCheckEvery.Should().Be(4);
        saved.UpdatrCheckEvery.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(UserSettings.MaxCheckHours + 1)]
    public async Task AnIntervalOutOfRangeIsRefused(int hours)
    {
        Func<Task> act = () => new ChangeSettings(new SettingsStore(new MemoryDataStore())).RunAsync(new SettingsChange(AppCheckHours: hours), new RecordingSink(), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AQuietListingAsksEachSourceForItsQuietList()
    {
        QuietSource source = new();
        ListPackages list = new([source], _ => Task.FromResult(new HoldPolicy([])));

        await list.RunAsync(new ListRequest(Quietly: true), new RecordingSink(), Ct);
        await list.RunAsync(new ListRequest(), new RecordingSink(), Ct);

        source.Calls.Should().Equal("quiet", "normal");
    }

    private sealed class QuietSource : IPackageSource
    {
        public List<string> Calls { get; } = [];

        public string Id => "fake";

        public async IAsyncEnumerable<InstalledPackage> ListInstalledAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("normal");
            await Task.Yield();
            yield break;
        }

        public async IAsyncEnumerable<InstalledPackage> ListInstalledQuietlyAsync(IOperationSink sink, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add("quiet");
            await Task.Yield();
            yield break;
        }

        public Task<InstalledPackage?> FindInstalledAsync(string id, CancellationToken cancellationToken) => Task.FromResult<InstalledPackage?>(null);

        public Task<PackageOutcome> UpgradeAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken) => Task.FromResult(PackageOutcome.Ok());

        public Task<PackageOutcome> ReinstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken) => Task.FromResult(PackageOutcome.Ok());

        public Task<PackageOutcome> UninstallAsync(InstalledPackage package, IOperationSink sink, CancellationToken cancellationToken) => Task.FromResult(PackageOutcome.Ok());
    }
}
