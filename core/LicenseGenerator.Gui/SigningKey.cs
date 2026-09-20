using System.Security.Cryptography;
using SentinelCore.Licensing;

namespace LicenseGenerator.Gui;

// What the GUI knows about a private.pem the user has pointed at. The fingerprint comparison is
// the whole reason this type exists: a key that doesn't match the public half compiled into
// SentinelCore.Licensing will still happily sign a file, and that file will look completely
// normal - it just gets rejected by every install on earth. The CLI can't catch that mistake
// before you've emailed the result to a paying customer; a window that's already holding the key
// can, so it does, the moment the key is selected.
public sealed record SigningKey
{
    private SigningKey(string path, string fingerprint, bool matchesShippedPublicKey)
    {
        FilePath = path;
        Fingerprint = fingerprint;
        MatchesShippedPublicKey = matchesShippedPublicKey;
    }

    public string FilePath { get; }

    // SHA-256 of the DER SubjectPublicKeyInfo of this key's public half, lowercase hex. The same
    // number `openssl pkey -pubout -outform DER | sha256sum` prints, so it can be checked against
    // the key on the server by hand.
    public string Fingerprint { get; }

    public bool MatchesShippedPublicKey { get; }

    // The fingerprint every issued license has to carry to be worth anything - derived from the
    // constant the app and the push relay actually verify against rather than written down here,
    // so rotating the key can never leave a stale expectation behind in this file.
    public static string ShippedPublicKeyFingerprint { get; } = ComputeShippedFingerprint();

    public static SigningKey Load(string path)
    {
        // Distinguishing these up front is worth it: "you picked public.pem" and "that file is
        // not a key at all" send you to completely different places, and they sit next to each
        // other in the same directory.
        string pem;
        try
        {
            pem = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SigningKeyException($"Couldn't read that file: {ex.Message}");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new SigningKeyException("That file isn't a PEM key. Pick the private.pem you generated for signing licenses.");
        }

        try
        {
            rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException)
        {
            throw new SigningKeyException("That's a public key - it can verify licenses but can't sign them. Pick private.pem instead.");
        }

        var fingerprint = FingerprintOf(rsa);
        return new SigningKey(path, fingerprint, fingerprint == ShippedPublicKeyFingerprint);
    }

    // Signs and writes in one step, but only after the destination is known to be writable -
    // a half-written .lic is worse than none, because it looks like a file you can send.
    public LicenseInfo Issue(string customerName, string customerEmail, string product, string outputPath)
    {
        var info = new LicenseInfo(
            customerName.Trim(),
            customerEmail.Trim(),
            string.IsNullOrWhiteSpace(product) ? "SentinelCore" : product.Trim(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(FilePath));

        var licenseText = LicenseSigner.Sign(info, rsa);
        File.WriteAllText(outputPath, licenseText);
        return info;
    }

    private static string FingerprintOf(RSA key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static string ComputeShippedFingerprint()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(LicensePublicKey.Pem);
        return FingerprintOf(rsa);
    }
}

public sealed class SigningKeyException(string message) : Exception(message);
