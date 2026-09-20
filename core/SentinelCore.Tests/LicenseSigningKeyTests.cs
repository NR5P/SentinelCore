using System.Security.Cryptography;
using LicenseGenerator.Gui;
using SentinelCore.Licensing;
using Xunit;

namespace SentinelCore.Tests;

// Covers the one thing the licensing GUI does that the CLI doesn't: telling you, before you sign
// anything, whether the key you picked is the key SentinelCore actually verifies against. A
// license signed with the wrong key is a well-formed, correctly signed file that no install will
// accept, and nothing about it looks wrong until a paying customer tries to use it.
public class LicenseSigningKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sentinelcore-key-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ShippedFingerprint_MatchesTheCompiledPublicKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(LicensePublicKey.Pem);
        var expected = Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

        // Deliberately recomputed here rather than pasted as a constant: this asserts that the
        // GUI derives its expectation from LicensePublicKey, so rotating the key can never leave
        // the warning comparing against a stale value.
        Assert.Equal(expected, SigningKey.ShippedPublicKeyFingerprint);
    }

    [Fact]
    public void ShippedFingerprint_IsTheSameNumberOpensslPrints()
    {
        // `openssl pkey -in private.pem -pubout -outform DER | shasum -a 256` of the key that
        // signs real licenses. Pinned so the fingerprint stays checkable by hand against the key
        // on the server, which is the whole point of showing it.
        Assert.Equal(
            "dd9e24ca9976c76b51fbc56e47097683967599edd40198fda84e7237db48c93b",
            SigningKey.ShippedPublicKeyFingerprint);
    }

    [Fact]
    public void AFreshlyGeneratedKey_IsReportedAsNotMatching()
    {
        var pair = KeyPairFactory.Generate(_dir);
        var key = SigningKey.Load(pair.PrivateKeyPath);

        Assert.False(key.MatchesShippedPublicKey);
        Assert.NotEqual(SigningKey.ShippedPublicKeyFingerprint, key.Fingerprint);
    }

    [Fact]
    public void ALicenseSignedWithTheWrongKey_IsRejectedByTheVerifier()
    {
        var pair = KeyPairFactory.Generate(_dir);
        var key = SigningKey.Load(pair.PrivateKeyPath);
        var output = Path.Combine(_dir, "wrong-key.lic");

        key.Issue("Jane Doe", "jane@example.com", "SentinelCore", output);

        // This is exactly the failure the fingerprint warning exists to prevent: a file that was
        // produced without error and is worthless.
        Assert.Null(LicenseStatus.Verify(File.ReadAllText(output)));
    }

    [Fact]
    public void AnIssuedLicense_RoundTripsThroughItsOwnKey()
    {
        var pair = KeyPairFactory.Generate(_dir);
        var key = SigningKey.Load(pair.PrivateKeyPath);
        var output = Path.Combine(_dir, "jane.lic");

        var issued = key.Issue("  Jane Doe  ", " jane@example.com ", "SentinelCore", output);

        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(File.ReadAllText(pair.PublicKeyPath));
        var verified = LicenseVerifier.Verify(File.ReadAllText(output), publicKey);

        Assert.NotNull(verified);
        Assert.Equal("Jane Doe", verified.CustomerName);
        Assert.Equal("jane@example.com", verified.CustomerEmail);
        Assert.Equal(issued.LicenseId, verified.LicenseId);
    }

    [Fact]
    public void BlankProduct_FallsBackToSentinelCore()
    {
        var pair = KeyPairFactory.Generate(_dir);
        var key = SigningKey.Load(pair.PrivateKeyPath);
        var output = Path.Combine(_dir, "blank-product.lic");

        var issued = key.Issue("Jane Doe", "jane@example.com", "   ", output);

        Assert.Equal("SentinelCore", issued.Product);
    }

    [Fact]
    public void PointingAtAPublicKey_SaysSoRatherThanFailingAtSigningTime()
    {
        var pair = KeyPairFactory.Generate(_dir);

        var ex = Assert.Throws<SigningKeyException>(() => SigningKey.Load(pair.PublicKeyPath));
        Assert.Contains("public key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PointingAtSomethingThatIsntAKey_IsRejected()
    {
        var junk = Path.Combine(_dir, "notes.pem");
        File.WriteAllText(junk, "this is not a key");

        Assert.Throws<SigningKeyException>(() => SigningKey.Load(junk));
    }

    [Fact]
    public void GeneratingOverAnExistingKey_IsRefused()
    {
        var first = KeyPairFactory.Generate(_dir);
        var before = File.ReadAllText(first.PrivateKeyPath);

        // Overwriting would silently invalidate every license already issued, with no record of
        // who they went to.
        Assert.Throws<SigningKeyException>(() => KeyPairFactory.Generate(_dir));
        Assert.Equal(before, File.ReadAllText(first.PrivateKeyPath));
    }

    [Fact]
    public void AGeneratedPrivateKey_IsNotReadableByAnyoneElse()
    {
        if (OperatingSystem.IsWindows()) return;

        var pair = KeyPairFactory.Generate(_dir);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(pair.PrivateKeyPath));
    }
}
