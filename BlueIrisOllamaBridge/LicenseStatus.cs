using System.Security.Cryptography;
using BlueIrisOllamaBridge.Licensing;

namespace BlueIrisOllamaBridge;

// Whole-app licensing, perpetual (no expiry) and unbound to any machine - a signed statement of
// who it was issued to, nothing more. Checked at startup purely for visibility (a log line, and
// the Web dashboard's nag banner via its own read of the same file) - never a functional gate.
// See LicenseGenerator/README.md for how licenses actually get issued.
public static class LicenseStatus
{
    // Generated via LicenseGenerator's `generate-keys` command. The matching private key never
    // touches this repo - it stays only wherever it was generated. Every install checks against
    // whatever is compiled in here, so this only ever needs to be set once.
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAoP5AaxG6j2hLHQvG7seg
        WGg4zFznO6LSdcz5oWRHFzuNF8LigifsJ/RLPeJnwNBgnt0CodfkZ/E0CSc1Hpx9
        kYMZLZQ48jaSIlxmLL4Zt2teGYTGHkDac/6VLyPVQESKjZPZ7V5GMsFf5mupz6zC
        pAAcVslomeBASvrW+23gxuMnXcJ7aFClkJlnfmRq2H1zpwBU+opHYdplgq+Vr7Xg
        nIvGxbFn8v7zj3f9aATmtWwbuPWNjCeZHFLrv6nnQoVgWcc9i5HAWT/1Wv1UYqfW
        obrXx6U+YFiVoRuAW+b/eVpJEoQPw4zU1D6jNldJTqcoNOrV5jMcte/zcao0AKuj
        gQRZ2gb+qXdao2rpAfczGFZlCEvucT/ZtCPb+jQVlv7qgrCPA7DYXG87fE/Yqzlt
        G52WeZZ0G/EHp2kJljcNaCg//4P9nASAnqhLuj0xDLJMuAN700FC4b/RZvaQ2CB2
        s97CzJ/AlJvqfymwsgjQQUqpH5lLUq5zOuSXYbyPxdXNAgMBAAE=
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
