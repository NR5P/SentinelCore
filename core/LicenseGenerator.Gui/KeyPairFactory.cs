using System.Security.Cryptography;

namespace LicenseGenerator.Gui;

// The one-time setup half of the tool, kept deliberately hard to reach in the UI. Generating a
// second key pair is not a mistake you can walk back: every license already issued against the
// old one stops verifying the moment the new public key ships, and there is no list of who those
// customers were. Hence the same refuse-to-overwrite rule the CLI has.
public static class KeyPairFactory
{
    public sealed record GeneratedKeyPair(string PrivateKeyPath, string PublicKeyPath, string PublicKeyPem);

    public static GeneratedKeyPair Generate(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var privatePath = Path.Combine(outputDirectory, "private.pem");
        var publicPath = Path.Combine(outputDirectory, "public.pem");

        if (File.Exists(privatePath))
            throw new SigningKeyException(
                $"{privatePath} already exists - refusing to overwrite it. Generating a new pair would " +
                "invalidate every license you've already issued.");

        using var rsa = RSA.Create(3072);
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
        // Owner-only from the instant it exists: the default umask would happily leave the root
        // of trust for every license world-readable.
        TryRestrictToOwner(privatePath);
        File.WriteAllText(publicPath, publicPem);

        return new GeneratedKeyPair(privatePath, publicPath, publicPem);
    }

    private static void TryRestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort - a key that exists but is group-readable still beats failing the
            // generation outright, and the UI tells you to move it somewhere safe anyway.
        }
    }
}
