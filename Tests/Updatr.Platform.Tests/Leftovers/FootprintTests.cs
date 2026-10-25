using Microsoft.Win32;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;
using Updatr.Platform.Data;
using Updatr.Platform.Leftovers;

namespace Updatr.Platform.Tests.Leftovers;

public sealed class FootprintTests : IDisposable
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"Updatr.Tests.{Guid.NewGuid():N}");
    private readonly string _key = $"Updatr.Tests.{Guid.NewGuid():N}";
    private readonly TempFolder _data = new();

    [Fact]
    public async Task WhatAnInstallCreatesIsRecordedPerPackage()
    {
        Footprints footprints = new(new PortableDataStore(_data.Path), "pc");
        PackageKey package = new("winget", "Vendor.App");

        object? before = await footprints.BeforeAsync(TestContext.Current.CancellationToken);
        Directory.CreateDirectory(_folder);
        Registry.CurrentUser.CreateSubKey($@"Software\{_key}").Dispose();
        await footprints.AfterAsync(before, package, TestContext.Current.CancellationToken);

        IReadOnlyList<FootprintItem> recorded = await footprints.LoadAsync(package, TestContext.Current.CancellationToken);

        recorded.Should().Contain(new FootprintItem(LeftoverKind.Folder, _folder));
        recorded.Should().Contain(new FootprintItem(LeftoverKind.RegistryKey, $@"HKCU\Software\{_key}"));
        (await footprints.LoadAsync(new PackageKey("winget", "Other.App"), TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\{_key}", throwOnMissingSubKey: false);
        _data.Dispose();
    }
}
