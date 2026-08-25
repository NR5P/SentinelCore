extern alias WebHost;

using Xunit;
using PasswordHasher = WebHost::SentinelCore.Web.PasswordHasher;

namespace SentinelCore.Tests;

// Verify reads the algorithm, iteration count, salt and expected hash out of the stored string,
// so everything it works from came off disk. A row can reach AccountStore from a restored backup,
// a hand-edited database, or a write that was cut short, and none of those are obliged to be
// sane - which is why the method says a malformed stored hash is simply "doesn't match".
//
// It wasn't. A stored value ending in "." left a zero-length expected hash, Pbkdf2 was asked to
// derive zero bytes, and FixedTimeEquals compared two empty spans and agreed: that account
// accepted every password, including no password at all. A zero or negative iteration count threw
// ArgumentOutOfRangeException out of the login endpoint instead of returning false.
public sealed class PasswordHasherTests
{
    [Fact]
    public void ARealHash_MatchesItsOwnPasswordAndNothingElse()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", stored));
        Assert.False(PasswordHasher.Verify("Correct horse battery staple", stored));
        Assert.False(PasswordHasher.Verify("", stored));
    }

    [Fact]
    public void TwoHashesOfTheSamePassword_Differ_BecauseTheSaltIsPerHash()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    // The stored format carries its own iteration count precisely so raising the constant doesn't
    // invalidate existing passwords, which only holds if an older count still verifies.
    [Fact]
    public void AHashStoredUnderAnOlderIterationCount_StillVerifies()
    {
        var salt = Convert.ToBase64String(new byte[16]);
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
                "old password", new byte[16], 1000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32));

        Assert.True(PasswordHasher.Verify("old password", $"PBKDF2.SHA256.1000.{salt}.{expected}"));
    }

    // The one that mattered: no hash stored means no password accepted, not every password.
    [Fact]
    public void AStoredHashWithAnEmptyHash_AcceptsNothing()
    {
        const string emptyTail = "PBKDF2.SHA256.1000.c2FsdHNhbHRzYWx0c2E=.";

        Assert.False(PasswordHasher.Verify("", emptyTail));
        Assert.False(PasswordHasher.Verify("literally anything", emptyTail));
    }

    [Theory]
    [InlineData("PBKDF2.SHA256.0.c2FsdHNhbHRzYWx0c2E=.aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGE=")]    // zero iterations
    [InlineData("PBKDF2.SHA256.-5.c2FsdHNhbHRzYWx0c2E=.aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGE=")]   // negative
    [InlineData("PBKDF2.SHA256.999999999999.c2FsdA==.aGFzaA==")]                                  // doesn't fit an int
    [InlineData("PBKDF2.SHA256.900000000.c2FsdA==.aGFzaA==")]                                     // absurd but parseable
    [InlineData("PBKDF2.SHA256.1000..aGFzaGhhc2hoYXNoaGFzaGhhc2hoYXNoaGE=")]                      // no salt
    [InlineData("PBKDF2.SHA256.notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("PBKDF2.SHA512.1000.c2FsdA==.aGFzaA==")]                                          // wrong algorithm
    [InlineData("PBKDF2.SHA256.1000.!!!not base64!!!.aGFzaA==")]
    [InlineData("PBKDF2.SHA256.1000.c2FsdA==")]                                                   // too few parts
    [InlineData("not-a-hash")]
    [InlineData("")]
    public void AMalformedStoredHash_SaysNoRatherThanThrowing(string stored)
    {
        Assert.False(PasswordHasher.Verify("any password", stored));
    }

    // A count read off disk decides how much work a single login attempt costs, so it needs a
    // ceiling as well as a floor - otherwise one row could hold a core for as long as it liked.
    [Fact]
    public void AnAbsurdIterationCount_IsRefusedQuickly()
    {
        var started = Environment.TickCount64;

        Assert.False(PasswordHasher.Verify("any password", "PBKDF2.SHA256.2000000000.c2FsdA==.aGFzaA=="));

        Assert.True(
            Environment.TickCount64 - started < 2000,
            "Refusing an absurd iteration count should be immediate, not derived.");
    }
}
