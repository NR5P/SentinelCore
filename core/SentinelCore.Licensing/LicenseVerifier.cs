using System.Security.Cryptography;
using System.Text.Json;

namespace SentinelCore.Licensing;

// The only thing the shipped app ever links against - never LicenseSigner, which needs a
// private key. Deliberately never throws: a missing, corrupt, or hand-edited license file is
// just "invalid", not a crash.
public static class LicenseVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static LicenseInfo? Verify(string licenseFileContents, RSA publicKey)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(licenseFileContents, JsonOptions);
            if (string.IsNullOrEmpty(envelope?.Payload) || string.IsNullOrEmpty(envelope.Signature))
                return null;

            var payloadBytes = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);

            // Verify before trusting a single field of the payload - a forged/tampered file
            // never reaches JSON parsing of its claimed contents.
            if (!publicKey.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return null;

            return JsonSerializer.Deserialize<LicenseInfo>(payloadBytes, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            return null;
        }
    }
}
