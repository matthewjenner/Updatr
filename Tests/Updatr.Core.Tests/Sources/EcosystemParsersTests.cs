using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

/// <summary>Fixtures are trimmed copies of real output captured on the dev machine (Phase 4).</summary>
public sealed class EcosystemParsersTests
{
    [Fact]
    public void NpmListReadsGlobalPackages()
    {
        const string json = """
            {
              "resolved": "file:../Users/matt/AppData/Roaming/nvm/v24.18.0",
              "dependencies": {
                "corepack": { "version": "0.35.0", "overridden": false },
                "npm": { "version": "11.16.0", "overridden": false }
              }
            }
            """;

        EcosystemParsers.NpmList(json).Should().Equal(new ToolPackage("corepack", "0.35.0"), new ToolPackage("npm", "11.16.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    public void NpmOutdatedEmptyMeansNothing(string json)
    {
        EcosystemParsers.NpmOutdated(json).Should().BeEmpty();
    }

    [Fact]
    public void NpmOutdatedReadsLatest()
    {
        const string json = """{ "typescript": { "current": "5.6.2", "wanted": "5.6.2", "latest": "7.0.2", "location": "C:\\x" } }""";

        EcosystemParsers.NpmOutdated(json)["typescript"].Should().Be("7.0.2");
    }

    [Fact]
    public void DotnetToolListReadsTheTable()
    {
        const string text = """
            Package Id         Version         Commands
            ----------------------------------------------
            csharpier          1.0.2           csharpier
            dotnet-dump        9.0.661903      dotnet-dump
            media-manager      1.0.0           mm
            """;

        EcosystemParsers.DotnetToolList(text).Select(t => t.Name).Should().Equal("csharpier", "dotnet-dump", "media-manager");
        EcosystemParsers.DotnetToolList(text)[1].Version.Should().Be("9.0.661903");
    }

    [Fact]
    public void NuGetPicksTheNewestStableNotTheLastListed()
    {
        const string json = """{ "versions": [ "0.9.0", "1.0.2", "1.10.0", "1.9.0", "2.0.0-beta.1" ] }""";

        EcosystemParsers.NuGetLatestStable(json).Should().Be("1.10.0");
    }

    [Fact]
    public void PipxListReadsMainPackages()
    {
        const string json = """
            { "pipx_spec_version": "0.1", "venvs": {
                "black": { "metadata": { "main_package": { "package": "black", "package_version": "24.4.2" } } },
                "ruff": { "metadata": { "main_package": { "package": "ruff", "package_version": "0.6.0" } } } } }
            """;

        EcosystemParsers.PipxList(json).Should().Equal(new ToolPackage("black", "24.4.2"), new ToolPackage("ruff", "0.6.0"));
    }

    [Fact]
    public void PyPiReadsLatest()
    {
        EcosystemParsers.PyPiLatest("""{ "info": { "name": "black", "version": "26.10.1" }, "releases": {} }""").Should().Be("26.10.1");
    }

    [Fact]
    public void PsGalleryReadsVersionFromTheODataFeed()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><m:properties><d:Version>1.29.380</d:Version><d:NormalizedVersion>1.29.380</d:NormalizedVersion><d:IsLatestVersion m:type="Edm.Boolean">true</d:IsLatestVersion></m:properties></entry>
            </feed>
            """;

        EcosystemParsers.PsGalleryLatest(xml).Should().Be("1.29.380");
    }

    [Fact]
    public void PsGetMetadataNamesTheRepository()
    {
        const string clixml = """<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj RefId="0"><MS><S N="Name">Microsoft.WinGet.Client</S><S N="Type">Module</S><S N="Repository">PSGallery</S></MS></Obj></Objs>""";

        EcosystemParsers.PsGetRepository(clixml).Should().Be("PSGallery");
        EcosystemParsers.PsGetRepository("<Objs/>").Should().BeNull();
    }

    [Fact]
    public void VsCodeListReadsIdAtVersion()
    {
        const string text = "anthropic.claude-code@2.1.309\r\nbradlc.vscode-tailwindcss@0.16.0\r\n\r\n";

        EcosystemParsers.VsCodeList(text).Should().Equal(new ToolPackage("anthropic.claude-code", "2.1.309"), new ToolPackage("bradlc.vscode-tailwindcss", "0.16.0"));
    }

    [Fact]
    public void VsCodeMarketplacePrefersThisPlatformOverAnOlderUniversalBuild()
    {
        // Real shape: per-platform 2.1.310 entries plus a stale universal 2.1.89.
        const string json = """
            { "results": [ { "extensions": [
              { "publisher": { "publisherName": "anthropic" }, "extensionName": "claude-code", "versions": [
                  { "version": "2.1.310", "targetPlatform": "win32-x64" },
                  { "version": "2.1.310", "targetPlatform": "linux-x64" },
                  { "version": "2.1.311", "targetPlatform": "darwin-arm64" },
                  { "version": "2.1.89" } ] },
              { "publisher": { "publisherName": "bradlc" }, "extensionName": "vscode-tailwindcss", "versions": [ { "version": "0.16.0" } ] } ] } ] }
            """;

        IReadOnlyDictionary<string, string> latest = EcosystemParsers.VsCodeMarketplace(json, "win32-x64").Latest;

        latest["anthropic.claude-code"].Should().Be("2.1.310", "not the darwin build, not the stale universal one");
        latest["bradlc.vscode-tailwindcss"].Should().Be("0.16.0");
    }

    [Fact]
    public void VsCodePreReleasesAreNeverOfferedAndAreFlaggedForAFullQuery()
    {
        // Real shape: a "latest only" query for GitLens returned just its newest pre-release.
        const string latestOnly = """
            { "results": [ { "extensions": [
              { "publisher": { "publisherName": "eamodio" }, "extensionName": "gitlens", "versions": [
                  { "version": "2026.10.200514", "properties": [ { "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" } ] } ] } ] } ] }
            """;
        const string fullHistory = """
            { "results": [ { "extensions": [
              { "publisher": { "publisherName": "eamodio" }, "extensionName": "gitlens", "versions": [
                  { "version": "2026.10.200514", "properties": [ { "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "true" } ] },
                  { "version": "19.4.0", "properties": [ { "key": "Microsoft.VisualStudio.Code.Engine", "value": "^1.92.0" } ] },
                  { "version": "19.3.0" } ] } ] } ] }
            """;

        EcosystemParsers.MarketplaceResult first = EcosystemParsers.VsCodeMarketplace(latestOnly, "win32-x64");
        first.Latest.Should().BeEmpty();
        first.OnlyPreRelease.Should().Equal("eamodio.gitlens");

        EcosystemParsers.VsCodeMarketplace(fullHistory, "win32-x64").Latest["eamodio.gitlens"].Should().Be("19.4.0");
    }

    [Theory]
    [InlineData("typescript", true)]
    [InlineData("@angular/cli", true)]
    [InlineData("ms-dotnettools.csharp", true)]
    [InlineData("evil&calc", false)]
    [InlineData("a|b", false)]
    [InlineData("%PATH%", false)]
    [InlineData("\"quoted\"", false)]
    [InlineData("", false)]
    public void OnlySafeNamesReachABatchFileCli(string name, bool safe)
    {
        EcosystemParsers.IsSafePackageName(name).Should().Be(safe);
    }
}

public sealed class VersionComparerTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "2.0.0-beta.2")]
    [InlineData("2.0.0-beta.10", "2.0.0-beta.2")]
    [InlineData("1.2.1", "1.2")]
    [InlineData("v3.0", "2.99")]
    [InlineData("8.0.32", "8.0.28")]
    public void NewerIsNewer(string newer, string older)
    {
        VersionComparer.IsNewer(newer, older).Should().BeTrue();
        VersionComparer.IsNewer(older, newer).Should().BeFalse();
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.0.0+build.5", "1.0.0")]
    public void EquivalentVersionsAreEqual(string a, string b)
    {
        VersionComparer.Instance.Compare(a, b).Should().Be(0);
    }
}
