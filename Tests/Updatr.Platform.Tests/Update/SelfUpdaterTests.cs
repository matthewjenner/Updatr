using System.Runtime.InteropServices;
using Updatr.Platform.Update;

namespace Updatr.Platform.Tests.Update;

public sealed class SelfUpdaterTests : IDisposable
{
    private readonly TempFolder _source = new();
    private readonly TempFolder _target = new();

    private void Write(TempFolder folder, string contents, params string[] files)
    {
        foreach (string file in files)
            File.WriteAllText(Path.Combine(folder.Path, file), contents);
    }

    private string Read(string file) => File.ReadAllText(Path.Combine(_target.Path, file));

    [Fact]
    public void SwapReplacesEveryReleaseFileAndKeepsTheOldOnesAside()
    {
        Write(_target, "old", [.. SelfUpdater.ReleaseFiles]);
        Write(_source, "new", [.. SelfUpdater.ReleaseFiles]);

        SelfUpdater.Swap(_source.Path, _target.Path);

        SelfUpdater.ReleaseFiles.Select(Read).Should().AllBe("new");
        File.ReadAllText(Path.Combine(_target.Path, "Updatr.exe.old")).Should().Be("old");
    }

    [Fact]
    public void ARunningExeIsRenamedNotOverwritten()
    {
        Write(_target, "old", [.. SelfUpdater.ReleaseFiles]);
        Write(_source, "new", [.. SelfUpdater.ReleaseFiles]);

        // Like a running image: open, sharing delete (rename) but not write.
        using (new FileStream(Path.Combine(_target.Path, "Updatr.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            SelfUpdater.Swap(_source.Path, _target.Path);

        Read("Updatr.exe").Should().Be("new");
    }

    [Fact]
    public void AFailedSwapPutsEverythingBack()
    {
        Write(_target, "old", [.. SelfUpdater.ReleaseFiles]);
        Write(_source, "new", "Updatr.exe", "updatr-cli.exe"); // The .winmd is missing: the copy fails part-way.

        Action act = () => SelfUpdater.Swap(_source.Path, _target.Path);

        act.Should().Throw<FileNotFoundException>();
        SelfUpdater.ReleaseFiles.Select(Read).Should().AllBe("old");
        Directory.EnumerateFiles(_target.Path, "*.old*").Should().BeEmpty();
    }

    [Fact]
    public void CleanUpRemovesOnlyOldReleaseFiles()
    {
        Write(_target, "x", "Updatr.exe.old", "updatr-cli.exe.old2", "notes.old", "Updatr.exe");

        SelfUpdater.CleanUpOld(_target.Path);

        Directory.EnumerateFiles(_target.Path).Select(Path.GetFileName).Should().BeEquivalentTo("notes.old", "Updatr.exe");
    }

    [Fact]
    public void ChecksumsAreReadFromSha256sumOutput()
    {
        string hash = new('a', 64);

        IReadOnlyDictionary<string, string> sums = SelfUpdater.ParseSums($"{hash}  Updatr-1.0.0-win-x64.zip\r\n{new string('b', 64)} *Updatr-1.0.0-win-arm64.zip\njunk line\n");

        sums["Updatr-1.0.0-win-x64.zip"].Should().Be(hash);
        sums["Updatr-1.0.0-win-arm64.zip"].Should().Be(new string('b', 64));
        sums.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(Architecture.X64, "Updatr-0.1.0-win-x64.zip")]
    [InlineData(Architecture.Arm64, "Updatr-0.1.0-win-arm64.zip")]
    public void TheAssetMatchesWhatPublishShNames(Architecture architecture, string expected)
        => SelfUpdater.AssetName("0.1.0", architecture).Should().Be(expected);

    [Fact]
    public void DevelopmentBuildsNeverUpdateThemselves()
    {
        Write(_target, "x", "Updatr.Core.dll");

        SelfUpdater.DevelopmentBuildReason("0.0.0", _source.Path).Should().NotBeNull();
        SelfUpdater.DevelopmentBuildReason("0.1.0", _target.Path).Should().NotBeNull();
        SelfUpdater.DevelopmentBuildReason("0.1.0", _source.Path).Should().BeNull();
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }
}
