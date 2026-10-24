using Updatr.Platform.DirectUrl;

namespace Updatr.Platform.Tests.DirectUrl;

/// <summary>
/// Against real files: the .NET host is embedded-signed by Microsoft with the certificate name
/// ".NET" (measured in Phase 4) - a reminder that catalog signers must be the exact CN.
/// </summary>
public sealed class AuthenticodeTests : IDisposable
{
    private static readonly string SignedFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ASignedFileIsTrustedAndNamesItsSigner()
    {
        Assert.SkipUnless(File.Exists(SignedFile), "needs the .NET host installed");

        SignatureCheck check = Authenticode.Check(SignedFile);

        check.Trusted.Should().BeTrue(check.Problem);
        check.Signer.Should().Be(".NET");
        Authenticode.Require(SignedFile, ".net").Should().BeNull("signer names compare case-insensitively");
    }

    [Fact]
    public void TheWrongExpectedSignerIsRefused()
    {
        Assert.SkipUnless(File.Exists(SignedFile), "needs the .NET host installed");

        Authenticode.Require(SignedFile, "Some Other Vendor").Should().Contain("signed by '.NET'");
    }

    [Fact]
    public void AnUnsignedFileIsRefused()
    {
        string file = Path.Combine(_temp.Path, "unsigned.exe");
        File.WriteAllBytes(file, [0x4D, 0x5A, 0, 0, 1, 2, 3]);

        SignatureCheck check = Authenticode.Check(file);

        check.Trusted.Should().BeFalse();
        Authenticode.Require(file, ".NET").Should().Contain("not trusted");
    }

    [Fact]
    public void ATamperedSignedFileIsRefused()
    {
        Assert.SkipUnless(File.Exists(SignedFile), "needs the .NET host installed");

        string copy = Path.Combine(_temp.Path, "tampered.exe");
        byte[] bytes = File.ReadAllBytes(SignedFile);
        bytes[bytes.Length / 3] ^= 0xFF; // Inside the signed content, well before the signature.
        File.WriteAllBytes(copy, bytes);

        SignatureCheck check = Authenticode.Check(copy);

        check.Trusted.Should().BeFalse();
        check.Problem.Should().Contain("changed after signing");
    }
}
