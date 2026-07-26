using System.Security.Cryptography;

namespace BlueIrisOllamaBridge.Web;

// Hand-rolled PBKDF2 rather than pulling in ASP.NET Core Identity for one shared dashboard
// password - matches this codebase's existing style of using BCL crypto primitives directly
// (see BlueIrisOllamaBridge.Licensing's RSA signing, ApiKeyMiddleware's FixedTimeEquals).
public static class PasswordHasher
{
    private const string Prefix = "PBKDF2.SHA256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

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

            var iterations = int.Parse(parts[2]);
            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);

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
