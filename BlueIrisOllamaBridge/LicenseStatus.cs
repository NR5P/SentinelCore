using System.Security.Cryptography;
using BlueIrisOllamaBridge.Licensing;

namespace BlueIrisOllamaBridge;

// Whole-app licensing, perpetual (no expiry) and unbound to any machine - a signed statement of
// who it was issued to, nothing more. Checked at startup purely for visibility (a log line, and
// the Web dashboard's nag banner via its own read of the same file) - never a functional gate.
// See LicenseGenerator/README.md for how licenses actually get issued.
public static class LicenseStatus
{
    // Placeholder until a real key pair exists - see LicenseGenerator's `generate-keys` command,
    // which prints the real public key to paste in here. Every install checks against whatever
    // is compiled in here, so this only ever needs to be set once, before the first real license
    // is ever issued.
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        REPLACE-ME-WITH-THE-OUTPUT-OF-LicenseGenerator-generate-keys
        -----END PUBLIC KEY-----
        """;

    public static LicenseInfo? Check(string licenseFilePath)
    {
        if (!File.Exists(licenseFilePath)) return null;

        try
        {
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(PublicKeyPem);
            var contents = File.ReadAllText(licenseFilePath);
            return LicenseVerifier.Verify(contents, publicKey);
        }
        catch
        {
            // A placeholder/malformed embedded public key, or a file that can't be read, is
            // just "no valid license" - never a startup crash over licensing.
            return null;
        }
    }
}
