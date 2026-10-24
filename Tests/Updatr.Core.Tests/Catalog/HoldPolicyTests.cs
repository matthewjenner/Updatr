using Updatr.Core.Catalog;

namespace Updatr.Core.Tests.Catalog;

/// <summary>
/// Hold behavior, checked against the real catalog/holds.json so a bad edit to the file shows up
/// here, not as a surprise upgrade.
/// </summary>
public sealed class HoldPolicyTests
{
    private static readonly IReadOnlyList<HoldRule> CatalogHolds = TestCatalog.LoadRepositoryCatalog().Holds;

    [Theory]
    [InlineData("OpenJS.NodeJS.LTS")]
    [InlineData("PostgreSQL.PostgreSQL.16")]
    [InlineData("Kitware.CMake")]
    [InlineData("Git.Git")]
    [InlineData("Nvidia.CUDA")]
    [InlineData("Microsoft.VCRedist.2015+.x64")]
    [InlineData("Microsoft.WSL")]
    public void VersionSensitivePackagesAreHeld(string packageId)
    {
        HoldMatch? match = new HoldPolicy(CatalogHolds).Match("winget", packageId);

        match.Should().NotBeNull();
        match!.Rule.Behavior.Should().Be(HoldBehavior.Hold);
        match.IsUserRule.Should().BeFalse();
    }

    [Theory]
    [InlineData("Python.Python.3.12")]
    [InlineData("Microsoft.DotNet.SDK.8")]
    [InlineData("EclipseAdoptium.Temurin.21.JDK")]
    [InlineData("Microsoft.VisualStudioCode")]
    public void RuntimesThatPinTheirMajorAreNotHeld(string packageId)
    {
        // The guiding rule: an id that pins the major stays within it on upgrade, so it is safe.
        new HoldPolicy(CatalogHolds).Match("winget", packageId).Should().BeNull();
    }

    [Fact]
    public void AppInstallerIsStoreManagedNotHeld()
    {
        HoldMatch? match = new HoldPolicy(CatalogHolds).Match("winget", "Microsoft.AppInstaller");

        match!.Rule.Behavior.Should().Be(HoldBehavior.StoreManaged);
    }

    [Fact]
    public void RulesOnlyApplyToTheirSource()
    {
        new HoldPolicy(CatalogHolds).Match("npm", "OpenJS.NodeJS.LTS").Should().BeNull();
    }

    [Fact]
    public void AUserReleaseCancelsACatalogHold()
    {
        HoldPolicy policy = new(CatalogHolds, userReleases: [("winget", "Git.Git")]);

        policy.Match("winget", "Git.Git").Should().BeNull();
        policy.Match("winget", "Kitware.CMake").Should().NotBeNull();
    }

    [Fact]
    public void AUserHoldWinsOverAUserRelease()
    {
        HoldRule mine = new() { Pattern = "Git.Git", Group = "Mine", Reason = "Pinned for a project" };
        HoldPolicy policy = new(CatalogHolds, userHolds: [mine], userReleases: [("winget", "Git.*")]);

        HoldMatch? match = policy.Match("winget", "Git.Git");

        match!.IsUserRule.Should().BeTrue();
        match.Rule.Reason.Should().Be("Pinned for a project");
    }
}
