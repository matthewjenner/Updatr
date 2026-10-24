using System.Text.Json;
using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

/// <summary>
/// The JSON shape is user-visible (hand-edited catalog, saved carts), so writing must produce
/// exactly what reading expects - no computed members, stable discriminators, defaults that survive.
/// </summary>
public sealed class CatalogJsonTests
{
    private static readonly CatalogApp Sample = new()
    {
        Id = "tsc",
        Name = "TypeScript",
        Category = "dev",
        Sources =
        [
            new NpmSourceRef { Package = "typescript" },
            new WingetSourceRef { Id = "Example.Tsc" },
            new DirectUrlSourceRef
            {
                GithubRelease = new GitHubReleaseRef { Repo = "owner/tsc", AssetPattern = "tsc-*.msi" },
                InstallerType = InstallerType.Msi,
                Signer = "Owner Inc",
            },
        ],
    };

    [Fact]
    public void ComputedMembersAreNotWritten()
    {
        string json = Write(Sample);

        json.Should().NotContain("requiredTool").And.NotContain("sourceId");
    }

    [Fact]
    public void DiscriminatorsAndEnumsUseTheirStableLowercaseNames()
    {
        string json = Write(Sample);

        json.Should().Contain("\"$type\": \"npm\"")
            .And.Contain("\"$type\": \"winget\"")
            .And.Contain("\"$type\": \"directUrl\"")
            .And.Contain("\"installerType\": \"msi\"");
    }

    [Fact]
    public void WrittenJsonReadsBackTheSame()
    {
        AppsFile file = new() { SchemaVersion = 1, Apps = [Sample] };

        AppsFile back = JsonSerializer.Deserialize<AppsFile>(JsonSerializer.Serialize(file, CatalogJson.Options), CatalogJson.StrictOptions)!;
        CatalogApp app = back.Apps.Single();

        app.Sources.Select(s => s.SourceId).Should().Equal("npm", "winget", "directUrl");
        app.Sources.OfType<DirectUrlSourceRef>().Single().GithubRelease!.AssetPattern.Should().Be("tsc-*.msi");
    }

    [Fact]
    public void OmittedOptionalMembersKeepTheirDefaults()
    {
        AppsFile file = JsonSerializer.Deserialize<AppsFile>(
            TestCatalog.AppsJson("""{ "id": "git", "name": "Git", "category": "dev", "sources": [ { "$type": "winget", "id": "Git.Git" } ] }"""),
            CatalogJson.Options)!;

        CatalogApp app = file.Apps.Single();
        app.Provides.Should().BeEmpty();
        app.Requires.Should().BeEmpty();
        app.Sources.OfType<WingetSourceRef>().Single().Source.Should().Be("winget");
    }

    [Fact]
    public void ExplicitNullsAlsoFallBackToDefaults()
    {
        AppsFile file = JsonSerializer.Deserialize<AppsFile>(
            TestCatalog.AppsJson("""{ "id": "git", "name": "Git", "category": "dev", "provides": null, "sources": [ { "$type": "winget", "id": "Git.Git", "source": null } ] }"""),
            CatalogJson.Options)!;

        file.Apps.Single().Provides.Should().BeEmpty();
        file.Apps.Single().Sources.OfType<WingetSourceRef>().Single().Source.Should().Be("winget");
    }

    private static string Write(CatalogApp app)
        => JsonSerializer.Serialize(new AppsFile { SchemaVersion = 1, Apps = [app] }, CatalogJson.Options);
}
