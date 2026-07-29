using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore;

// Generates Bridge:ApiKey on first boot when it's missing or still the shipped
// "CHANGE-ME-..." placeholder, so a fresh install works immediately instead of crashing on
// BridgeOptions' [Required] validation - the same "no manual editing" treatment
// AutoCertificateProvisioner gives the HTTPS certificate. Unlike that certificate, this value
// has to be persisted rather than regenerated every boot: Blue Iris is configured with it once,
// and a key that changed on every restart would silently break that integration.
public static class AutoApiKeyProvisioner
{
    public static string EnsureApiKey(string appSettingsPath, string? configuredApiKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredApiKey) &&
            !configuredApiKey.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase))
        {
            return configuredApiKey;
        }

        var generated = RandomNumberGenerator.GetHexString(64);
        Persist(appSettingsPath, generated);
        return generated;
    }

    private static void Persist(string appSettingsPath, string apiKey)
    {
        var root = JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject
            ?? throw new InvalidOperationException($"{appSettingsPath} does not contain a JSON object at its root.");

        if (root["Bridge"] is not JsonObject bridge)
        {
            bridge = new JsonObject();
            root["Bridge"] = bridge;
        }
        bridge["ApiKey"] = apiKey;

        // Same encoder choice as AppSettingsFile in the Web project, for the same reason: the
        // default encoder escapes ordinary characters like apostrophes, which would rewrite
        // dozens of unrelated PromptProfiles lines just from generating this key once.
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var temporaryPath = appSettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(options));
        File.Move(temporaryPath, appSettingsPath, overwrite: true);
    }
}
