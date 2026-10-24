using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

public sealed class CatalogValidatorTests
{
    private static IReadOnlyList<string> Issues(string appsArrayBody)
        => CatalogLoader.Parse(TestCatalog.Minimal(apps: TestCatalog.AppsJson(appsArrayBody), templates: """{ "schemaVersion": 1, "templates": [] }"""), CatalogJson.Options).Issues;

    [Theory]
    [InlineData("Git")]
    [InlineData("git--scm")]
    [InlineData("git_scm")]
    [InlineData("-git")]
    public void IdsMustBeLowercaseKebab(string id)
    {
        Issues($$"""{ "id": "{{id}}", "name": "Git", "category": "dev", "sources": [ { "$type": "winget", "id": "Git.Git" } ] }""")
            .Should().ContainSingle().Which.Should().Contain("id must be");
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        const string app = """{ "id": "git", "name": "Git", "category": "dev", "sources": [ { "$type": "winget", "id": "Git.Git" } ] }""";

        Issues(app + "," + app).Should().ContainSingle().Which.Should().Contain("duplicate id");
    }

    [Fact]
    public void AnAppNeedsASource()
    {
        Issues("""{ "id": "git", "name": "Git", "category": "dev", "sources": [] }""")
            .Should().ContainSingle().Which.Should().Contain("at least one source");
    }

    [Fact]
    public void RequiresMustResolve()
    {
        Issues("""{ "id": "tsc", "name": "TS", "category": "dev", "requires": [ "node" ], "sources": [ { "$type": "npm", "package": "typescript" } ] }""")
            .Should().ContainSingle().Which.Should().Contain("requires unknown app 'node'");
    }

    [Fact]
    public void DirectUrlNeedsExactlyOneLocation()
    {
        Issues("""
            { "id": "tool", "name": "Tool", "category": "utilities", "sources": [
                { "$type": "directUrl", "url": "https://example.com/a.exe", "githubRelease": { "repo": "a/b", "assetPattern": "*.exe" },
                  "installerType": "exe", "signer": "Example" } ] }
            """).Should().ContainSingle().Which.Should().Contain("exactly one of url or githubRelease");
    }

    [Fact]
    public void DirectUrlMustBeHttps()
    {
        Issues("""
            { "id": "tool", "name": "Tool", "category": "utilities", "sources": [
                { "$type": "directUrl", "url": "http://example.com/a.exe", "installerType": "exe", "signer": "Example",
                  "mirrors": [ "ftp://mirror/a.exe" ] } ] }
            """).Should().HaveCount(2).And.Contain(i => i.Contains("url must be https")).And.Contain(i => i.Contains("must be https"));
    }

    [Fact]
    public void AValidDirectUrlPasses()
    {
        Issues("""
            { "id": "tool", "name": "Tool", "category": "utilities", "sources": [
                { "$type": "directUrl", "githubRelease": { "repo": "owner/tool", "assetPattern": "tool-*-x64.msi" },
                  "installerType": "msi", "signer": "Owner Inc", "mirrors": [] } ] }
            """).Should().BeEmpty();
    }
}
