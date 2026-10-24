using Updatr.Core.Errors;

namespace Updatr.Core.Tests.Errors;

public sealed class ErrorCodesTests
{
    [Theory]
    [InlineData(0x8A15002BL, "already current")]
    [InlineData(0x8A150011L, "hash mismatch")]
    [InlineData(0x8A150056L, "refuses to run elevated")]
    [InlineData(0x00000643L, "1603")]
    public void KnownCodesDecodeToTheirHint(long code, string fragment)
    {
        ErrorCodes.Describe(code).Hint.Should().Contain(fragment);
    }

    [Fact]
    public void NegativeHresultsAreMaskedTo32Bits()
    {
        // COMException.HResult is a signed int; 0x8A150011 arrives as a negative number.
        int hresult = unchecked((int)0x8A150011);

        ErrorCodeInfo info = ErrorCodes.Describe(hresult);

        info.Code.Should().Be(0x8A150011u);
        info.Hex.Should().Be("0x8A150011");
    }

    [Fact]
    public void UnknownCodesKeepTheirHexAndHaveNoHint()
    {
        ErrorCodeInfo info = ErrorCodes.Describe(0x8A15FFFF);

        info.Hint.Should().BeNull();
        info.Outcome.Should().Be(CodeOutcome.Failure);
        info.ToString().Should().Be("0x8A15FFFF");
    }

    [Theory]
    [InlineData(0x80190194L, 404, false)]
    [InlineData(0x801901F7L, 503, true)]
    [InlineData(0x801901ADL, 429, true)]
    public void HttpFacilityCodesCarryTheStatus(long code, int status, bool transient)
    {
        ErrorCodeInfo info = ErrorCodes.Describe(code);

        info.Hint.Should().Contain($"HTTP {status}");
        info.Transient.Should().Be(transient);
    }

    [Theory]
    [InlineData(0x8A15002BL)]
    [InlineData(0x8A15010DL)]
    [InlineData(0x8A150061L)]
    [InlineData(0x8A15004FL)]
    public void NothingToDoCodesAreNotFailures(long code)
    {
        ErrorCodes.Describe(code).Outcome.Should().Be(CodeOutcome.AlreadyCurrent);
    }

    [Theory]
    [InlineData(0x00000BC2L)]
    [InlineData(0x00000669L)]
    [InlineData(0x8A150109L)]
    [InlineData(0x8A15010BL)]
    public void RebootCodesAreSuccesses(long code)
    {
        ErrorCodes.Describe(code).Outcome.Should().Be(CodeOutcome.SuccessRebootRequired);
    }

    [Theory]
    [InlineData(0x00000652L)]
    [InlineData(0x8A150102L)]
    [InlineData(0x8A150103L)]
    [InlineData(0x8A150111L)]
    public void InUseAndBusyCodesAreTransient(long code)
    {
        ErrorCodes.Describe(code).Transient.Should().BeTrue();
    }
}
