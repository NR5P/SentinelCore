using System.Security.Cryptography;
using SentinelCore.Licensing;

namespace SentinelCore;

// Whole-app licensing, perpetual (no expiry) and unbound to any machine - a signed statement of
// who it was issued to, nothing more. Checked at startup purely for visibility (a log line, and
// the Web dashboard's nag banner via its own read of the same file) - never a functional gate.
// See LicenseGenerator/README.md for how licenses actually get issued.
public static class LicenseStatus
{
    // The embedded public half of the signing key now lives in the Licensing library
    // (LicensePublicKey) so the push relay can verify with the same key without referencing this
    // project. Kept as an alias here for the docs and tools that name it.
    public const string PublicKeyPem = LicensePublicKey.Pem;

    public static LicenseInfo? Check(string licenseFilePath)
    {
        if (!File.Exists(licenseFilePath)) return null;

        try
        {
            return Verify(File.ReadAllText(licenseFilePath));
        }
        catch
        {
            // A file that exists but can't be read (permissions, encoding, ...) is just "no
            // valid license" - never a startup crash over licensing.
            return null;
        }
    }

    // Verifies raw .lic contents directly - used before writing an uploaded file to disk, so a
    // garbled upload never overwrites a license that was already working.
    public static LicenseInfo? Verify(string licenseFileContents)
    {
        try
        {
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(PublicKeyPem);
            return LicenseVerifier.Verify(licenseFileContents, publicKey);
        }
        catch
        {
            // A placeholder/malformed embedded public key is also just "no valid license".
            return null;
        }
    }
}
