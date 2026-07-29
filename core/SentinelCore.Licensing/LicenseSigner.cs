using System.Security.Cryptography;
using System.Text.Json;

namespace SentinelCore.Licensing;

// Used only by the LicenseGenerator tool, which is the one thing that ever touches a private
// key - the app itself only ever links against LicenseVerifier.
public static class LicenseSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Sign(LicenseInfo info, RSA privateKey)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(info, JsonOptions);
        var signature = privateKey.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        var envelope = new LicenseEnvelope(
            Convert.ToBase64String(payloadBytes),
            Convert.ToBase64String(signature));

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }
}
