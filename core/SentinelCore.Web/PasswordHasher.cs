using System.Security.Cryptography;

namespace SentinelCore.Web;

// Hand-rolled PBKDF2 rather than pulling in ASP.NET Core Identity for one shared dashboard
// password - matches this codebase's existing style of using BCL crypto primitives directly
// (see SentinelCore.Licensing's RSA signing, ApiKeyMiddleware's FixedTimeEquals).
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2.SHA256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Far above Iterations so raising that never trips it, but bounded: the count is read from
    // the stored value, and an absurd one there would otherwise let a single login attempt hold a
    // core for as long as it liked.
    private const int MaximumIterations = 10_000_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    // Stored format is self-describing (algorithm + iteration count) precisely so a future
    // bump to Iterations doesn't invalidate passwords hashed under the old count - this always
    // re-derives using whatever iteration count is embedded in the stored value, not the
    // current constant.
    public static bool Verify(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 5 || parts[0] != "PBKDF2" || parts[1] != "SHA256")
                return false;

            // Everything below this line came off disk, so none of it can be assumed sane. A row
            // reaching AccountStore from a restored backup, a hand-edited database, or a write
            // that was cut short is exactly the case this method claims to answer with a plain
            // "doesn't match" - and it did not.
            //
            // The empty-hash case was the one that mattered: a stored value ending in "." parsed
            // to a zero-length expected hash, Pbkdf2 was then asked to derive zero bytes, and
            // FixedTimeEquals compared two empty spans and said yes. That account accepted every
            // password, including no password. A zero or negative iteration count threw
            // ArgumentOutOfRangeException straight out of the login endpoint instead.
            if (!int.TryParse(parts[2], out var iterations) || iterations <= 0 || iterations > MaximumIterations)
                return false;

            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            if (salt.Length == 0 || expected.Length == 0) return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // A malformed/corrupted stored hash is just "doesn't match" - never a 500 over it.
            return false;
        }
    }
}
