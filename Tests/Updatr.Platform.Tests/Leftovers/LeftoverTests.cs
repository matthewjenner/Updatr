using Microsoft.Win32;
using Updatr.Core.Leftovers;
using Updatr.Core.Sources;
using Updatr.Platform.Leftovers;

namespace Updatr.Platform.Tests.Leftovers;

public sealed class LeftoverRulesTests
{
    private static LeftoverTarget Target(string name, string? publisher = null)
        => new() { Package = new PackageKey("winget", "x"), Name = name, Publisher = publisher };

    [Theory]
    [InlineData("Everything 1.4.1.1032 (x64)", "everything")]
    [InlineData("Mozilla Firefox (x64 en-US)", "mozillafirefox")]
    [InlineData("7-Zip 26.05 (x64)", "7zip")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable", "microsoftvisualc20152022redistributable")]
    public void NamesAreReducedToTheirIdentity(string name, string key) => LeftoverRules.Key(name).Should().Be(key);

    [Fact]
    public void ThePublisherPrefixIsAlsoTried()
        => LeftoverRules.AppKeys(Target("Mozilla Firefox (x64 en-US)", "Mozilla Corporation")).Should().Equal("mozillafirefox", "firefox");

    [Theory]
    [InlineData("Everything", "Medium")]
    [InlineData("everything", "Medium")]
    [InlineData("Everything Search", "Low")]
    [InlineData("Data", null)]
    [InlineData("Microsoft", null)]
    [InlineData("Ev", null)]
    public void FolderNamesMatchConservatively(string folder, string? expected)
        => LeftoverRules.MatchName(folder, ["everything"])?.ToString().Should().Be(expected);

    [Fact]
    public void SharedAndSystemNamesNeverMatchEvenWhenTheAppIsNamedThat()
        => LeftoverRules.MatchName("Steam", LeftoverRules.AppKeys(Target("Steam"))).Should().BeNull();

    [Fact]
    public void OnlyFoldersOneOrTwoLevelsUnderARootAreOffered()
    {
        string[] roots = [@"C:\Program Files", @"C:\Users\u\AppData\Roaming"];

        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor", roots, []).Should().BeTrue();
        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor\App", roots, []).Should().BeTrue();
        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor\App\bin", roots, []).Should().BeFalse();
        LeftoverRules.IsOfferableFolder(@"C:\Program Files", roots, []).Should().BeFalse();
        LeftoverRules.IsOfferableFolder(@"C:\Temp\App", roots, []).Should().BeFalse();
    }

    [Fact]
    public void AFolderAroundOrInsideAnotherAppIsNeverOffered()
    {
        string[] roots = [@"C:\Program Files"];
        string[] otherApps = [@"C:\Program Files\Vendor\OtherApp"];

        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor", roots, otherApps).Should().BeFalse();
        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor\OtherApp", roots, otherApps).Should().BeFalse();
        LeftoverRules.IsOfferableFolder(@"C:\Program Files\Vendor\GoneApp", roots, otherApps).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\unins000.exe"" /SILENT", @"C:\Program Files\App\unins000.exe")]
    [InlineData(@"C:\Program Files\App\uninstall.exe /S", @"C:\Program Files\App\uninstall.exe")]
    [InlineData(@"MsiExec.exe /X{B5F500BB}", "MsiExec.exe")]
    public void TheExecutableIsReadFromACommandLine(string command, string exe) => LeftoverRules.ExecutableOf(command).Should().Be(exe);

    [Theory]
    [InlineData(@"HKCU\Software\Vendor", true)]
    [InlineData(@"HKCU\Software\Vendor\App", true)]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Vendor\App", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}", true)]
    [InlineData(@"HKCU\Software\Vendor\App\Deep", false)]
    [InlineData(@"HKCU\Software\Microsoft", false)]
    [InlineData(@"HKCU\Software\Microsoft\Office", false)]
    [InlineData(@"HKLM\SOFTWARE\Classes\Thing", false)]
    [InlineData(@"HKLM\SOFTWARE\Policies\Vendor", false)]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\X", false)]
    [InlineData(@"HKLM\SOFTWARE", false)]
    public void OnlyAppKeysMayBeQuarantined(string key, bool allowed) => LeftoverSafety.RegistryKeyAllowed(key).Should().Be(allowed);

    [Fact]
    public void WindowsTasksAndServicesAreRefused()
    {
        LeftoverSafety.Problem(LeftoverKind.ScheduledTask, @"\Microsoft\Windows\Defrag\ScheduledDefrag").Should().NotBeNull();
        LeftoverSafety.Problem(LeftoverKind.Service, "Dhcp").Should().NotBeNull(); // svchost under Windows
        LeftoverSafety.Problem(LeftoverKind.Service, "bad name & calc").Should().NotBeNull();
        LeftoverSafety.Problem(LeftoverKind.Shortcut, @"C:\Temp\x.lnk").Should().NotBeNull();
    }
}

/// <summary>A real move and restore of a folder and a registry key in this user's own profile.</summary>
public sealed class QuarantineEngineTests : IDisposable
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"Updatr.Tests.{Guid.NewGuid():N}");
    private readonly string _key = $@"Software\Updatr.Tests.{Guid.NewGuid():N}";
    private readonly TempFolder _entries = new();

    public QuarantineEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "sub"));
        File.WriteAllText(Path.Combine(_folder, "settings.ini"), "a=1");
        File.WriteAllText(Path.Combine(_folder, "sub", "cache.bin"), "xyz");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_key);
        key.SetValue("Name", "value");
        key.SetValue("Count", 7, RegistryValueKind.DWord);
        key.SetValue("Path", @"%APPDATA%\x", RegistryValueKind.ExpandString);
        using RegistryKey child = key.CreateSubKey("Child");
        child.SetValue("List", new[] { "a", "b" }, RegistryValueKind.MultiString);
    }

    private string EntryDir => Path.Combine(_entries.Path, "entry");

    private Leftover[] Items =>
    [
        new("1", LeftoverKind.Folder, _folder, Confidence.Medium, "test", 10),
        new("2", LeftoverKind.RegistryKey, $@"HKCU\{_key}", Confidence.Medium, "test"),
    ];

    [Fact]
    public void MovedItemsAreGoneAndComeBackExactly()
    {
        QuarantineEntry entry = QuarantineEngine.Move(EntryDir, "Test App", Items);

        entry.Items.Should().OnlyContain(i => i.Stored && i.Problem == null);
        Directory.Exists(_folder).Should().BeFalse();
        Registry.CurrentUser.OpenSubKey(_key).Should().BeNull();

        QuarantineEngine.Restore(EntryDir).Should().BeEmpty();

        File.ReadAllText(Path.Combine(_folder, "sub", "cache.bin")).Should().Be("xyz");
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(_key)!;
        key.GetValue("Count").Should().Be(7);
        key.GetValueKind("Path").Should().Be(RegistryValueKind.ExpandString);
        key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames).Should().Be(@"%APPDATA%\x");
        using RegistryKey child = key.OpenSubKey("Child")!;
        child.GetValue("List").Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void RestoreNeverOverwritesWhatIsThereAgain()
    {
        QuarantineEngine.Move(EntryDir, "Test App", Items);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.ini"), "new");

        IReadOnlyList<string> problems = QuarantineEngine.Restore(EntryDir);

        File.ReadAllText(Path.Combine(_folder, "settings.ini")).Should().Be("new");
        File.Exists(Path.Combine(_folder, "sub", "cache.bin")).Should().BeTrue();
        problems.Should().ContainSingle().Which.Should().Contain("already existed");
    }

    [Fact]
    public void ItemsOutsideTheAllowedPlacesAreLeftAlone()
    {
        string outside = Path.Combine(_entries.Path, "not-allowed");
        Directory.CreateDirectory(outside);

        QuarantineEntry entry = QuarantineEngine.Move(EntryDir, "Test App", [new("1", LeftoverKind.Folder, outside, Confidence.High, "test")]);

        entry.Items.Single().Stored.Should().BeFalse();
        Directory.Exists(outside).Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Moved and not restored.
        }

        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        _entries.Dispose();
    }
}
