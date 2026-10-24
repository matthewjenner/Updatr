using Updatr.Core.Sources;

namespace Updatr.Core.Tests.Sources;

public sealed class PackageOutcomeTests
{
    [Fact]
    public void ZeroIsAPlainSuccess()
    {
        PackageOutcome outcome = PackageOutcome.FromCode(0);

        outcome.Kind.Should().Be(OutcomeKind.Ok);
        outcome.RebootRequired.Should().BeFalse();
        outcome.Code.Should().BeNull();
    }

    [Fact]
    public void AlreadyCurrentIsNeverAFailure()
    {
        PackageOutcome outcome = PackageOutcome.FromCode(0x8A15002B);

        outcome.Kind.Should().Be(OutcomeKind.AlreadyCurrent);
        outcome.Detail.Should().Contain("0x8A15002B");
    }

    [Fact]
    public void RebootRequiredIsASuccessWithTheFlagSet()
    {
        PackageOutcome outcome = PackageOutcome.FromCode(3010);

        outcome.Kind.Should().Be(OutcomeKind.Ok);
        outcome.RebootRequired.Should().BeTrue();
    }

    [Fact]
    public void FailuresKeepTheDecodedCode()
    {
        PackageOutcome outcome = PackageOutcome.FromCode(1603);

        outcome.Kind.Should().Be(OutcomeKind.Failed);
        outcome.Code!.Hex.Should().Be("0x00000643");
        outcome.Detail.Should().Contain("1603");
    }
}
