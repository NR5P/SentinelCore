using System.Net.Http.Headers;
using System.Text.Json;
using SentinelCore;

namespace SentinelCore.Web;

// The cloud half of the Model dropdown - the counterpart to OllamaModelEndpoints, filling the same
// SettingsOptionsSourceSchema contract, plus the small static list behind the API Base URL box.
//
// Asking the provider what it has is the only approach that survives contact with a field that has
// to accept models nobody here has heard of. A hard-coded list of model names would be wrong within
// a month, and wrong in the direction that matters: it would be missing whatever shipped last week,
// which is exactly what somebody configuring this is reaching for.
//
// Both endpoints read straight out of the main app's appsettings.json rather than this project's
// own config, same as OllamaModelEndpoints - and for the models list that includes the API key,
// which is why this cannot be done in the browser. The key never leaves the server.
public sealed class CloudVisionModelLog;

public static class CloudVisionModelEndpoints
{
    private const string AnthropicVersion = "2023-06-01";

    // Fixed addresses for the two providers that speak their own dialect. Deliberately the same
    // literals Program.cs gives their HttpClients: if either ever moves, both have to move, and a
    // dropdown quietly listing a different host's models than the alert path calls would be a
    // genuinely confusing bug to chase.
    private const string AnthropicBaseUrl = "https://api.anthropic.com/v1/";
    private const string GoogleBaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    public static void MapCloudVisionModelEndpoints(this WebApplication app)
    {
        // AdminOnly to match the rest of /api/settings. This one also spends the account's key to
        // answer, which is a second reason not to leave it open.
        app.MapGet("/api/settings/cloud-vision/models", async (
            AppSettingsFile appSettings,
            IHttpClientFactory httpClientFactory,
            ILogger<CloudVisionModelLog> logger,
            CancellationToken ct) =>
        {
            var settings = await appSettings.GetSectionAsync<CloudVisionAccount>("CloudVision", ct);
            var provider = string.IsNullOrWhiteSpace(settings?.Provider) ? "openai" : settings.Provider.Trim();
            var apiKey = settings?.ApiKey?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(apiKey))
                return Results.Json(
                    new { error = "No API key is saved for this provider yet. Save the key first, then the list can be fetched - or type the model name." },
                    statusCode: 502);

            // "openai" is the only slot whose address is configurable, because it is the only one
            // that stands for more than one service - see CloudVisionOptions.Provider.
            var baseUrl = provider switch
            {
                "anthropic" => AnthropicBaseUrl,
                "google" => GoogleBaseUrl,
                _ => string.IsNullOrWhiteSpace(settings?.BaseUrl)
                    ? new CloudVisionOptions().BaseUrl
                    : settings.BaseUrl.Trim()
            };

            // Relative resolution replaces the last path segment without this, so a base URL typed
            // without its trailing slash would ask ".../api/models" instead of ".../api/v1/models".
            if (!baseUrl.EndsWith('/')) baseUrl += "/";

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
                return Results.Json(
                    new { error = $"The API Base URL is not a valid address ({baseUrl}). Fix it above, or type the model name." },
                    statusCode: 502);

            var client = httpClientFactory.CreateClient(nameof(CloudVisionModelEndpoints));

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "models"));
            switch (provider)
            {
                case "anthropic":
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", AnthropicVersion);
                    break;
                case "google":
                    request.Headers.Add("x-goog-api-key", apiKey);
                    break;
                default:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    break;
            }

            try
            {
                using var response = await client.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Cloud vision model list from {Provider} returned {StatusCode}", provider, (int)response.StatusCode);

                    // 401/403 is overwhelmingly the case worth naming precisely: it means the key
                    // is wrong or lacks access, which is the single most common reason this whole
                    // section silently does nothing.
                    var detail = (int)response.StatusCode is 401 or 403
                        ? "the saved API key was refused"
                        : $"it answered {(int)response.StatusCode}";
                    return Results.Json(
                        new { error = $"Couldn't list models - {detail}. Check the key and provider above, or type the model name." },
                        statusCode: 502);
                }

                return Results.Ok(new { options = ParseModels(provider, body) });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Could not read the model list from {Provider} at {BaseUrl}", provider, baseUrl);
                return Results.Json(
                    new { error = $"Couldn't reach {baseUrl} to list its models. Check the address and your connection, or type the model name." },
                    statusCode: 502);
            }
        })
        .RequireAuthorization("AdminOnly");

        // Static, and answered from here rather than embedded in each client, purely so the four
        // settings renderers need no new concept to show it - it rides the same options-source
        // mechanism the two model lists use. AdminOnly for consistency with its neighbours; there
        // is nothing secret in the list itself.
        app.MapGet("/api/settings/cloud-vision/base-urls", () => Results.Ok(new { options = KnownEndpoints }))
            .RequireAuthorization("AdminOnly");
    }

    // Each provider reports its models in its own shape, so this is three small mappings rather
    // than one. Public and taking the body as a string so each can be tested against a real
    // payload without a live account or a network call.
    public static List<SettingsFieldOption> ParseModels(string provider, string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];

        var options = provider switch
        {
            "google" => ParseGoogle(root),
            "anthropic" => ParseAnthropic(root),
            _ => ParseOpenAi(root)
        };

        options.Sort((left, right) => string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    // OpenAI: { "data": [ { "id": "gpt-4o-mini" } ] }. The id is exactly the string the setting
    // holds. No attempt is made to filter to vision-capable models: nothing in this response says
    // which those are, and for the OpenAI-compatible endpoints this list mostly exists to serve,
    // no such field could be relied on anyway. A wrong pick is what the test button is for.
    private static List<SettingsFieldOption> ParseOpenAi(JsonElement root)
    {
        var options = new List<SettingsFieldOption>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("id", out var id) ||
                id.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name))
                continue;

            options.Add(new SettingsFieldOption(name, name));
        }

        return options;
    }

    // Anthropic: { "data": [ { "id": "claude-...", "display_name": "Claude ..." } ] }. The id is
    // what the setting holds; display_name is friendlier to scan, so the label carries both.
    private static List<SettingsFieldOption> ParseAnthropic(JsonElement root)
    {
        var options = new List<SettingsFieldOption>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("id", out var id) ||
                id.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name))
                continue;

            var display = model.TryGetProperty("display_name", out var displayName)
                ? displayName.GetString()
                : null;

            options.Add(new SettingsFieldOption(
                name,
                string.IsNullOrWhiteSpace(display) || display == name ? name : $"{name} - {display}"));
        }

        return options;
    }

    // Google: { "models": [ { "name": "models/gemini-...", "supportedGenerationMethods": [...] } ] }.
    // Two differences that matter. The name carries a "models/" prefix the setting must NOT hold -
    // GoogleVisionClient builds "models/{Model}:generateContent" itself, so leaving it on would
    // produce "models/models/...". And this is the one provider that does say which models can be
    // called the way this app calls them, so the ones that can't are dropped rather than offered.
    private static List<SettingsFieldOption> ParseGoogle(JsonElement root)
    {
        var options = new List<SettingsFieldOption>();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("name", out var nameElement) ||
                nameElement.GetString() is not { } fullName ||
                string.IsNullOrWhiteSpace(fullName))
                continue;

            if (model.TryGetProperty("supportedGenerationMethods", out var methods) &&
                methods.ValueKind == JsonValueKind.Array &&
                !methods.EnumerateArray().Any(m => m.GetString() == "generateContent"))
                continue;

            var name = fullName.StartsWith("models/", StringComparison.Ordinal)
                ? fullName["models/".Length..]
                : fullName;

            var display = model.TryGetProperty("displayName", out var displayName)
                ? displayName.GetString()
                : null;

            options.Add(new SettingsFieldOption(
                name,
                string.IsNullOrWhiteSpace(display) || display == name ? name : $"{name} - {display}"));
        }

        return options;
    }

    // Convenience, not policy. Anything else speaking the same dialect works by typing its address
    // in, which is the case the escape hatch in the options-source contract exists for. Kept short
    // on purpose: a long list is harder to scan than a text box is to type into, and every entry
    // here is one more thing to be wrong about later.
    private static readonly IReadOnlyList<SettingsFieldOption> KnownEndpoints =
    [
        new("https://api.openai.com/v1/", "OpenAI - api.openai.com"),
        new("https://openrouter.ai/api/v1/", "OpenRouter - openrouter.ai"),
        new("https://api.groq.com/openai/v1/", "Groq - api.groq.com"),
        new("https://api.together.xyz/v1/", "Together AI - api.together.xyz"),
        new("https://api.fireworks.ai/inference/v1/", "Fireworks AI - api.fireworks.ai"),
        new("https://api.deepinfra.com/v1/openai/", "DeepInfra - api.deepinfra.com"),
        new("http://127.0.0.1:1234/v1/", "LM Studio - this machine, port 1234"),
        new("http://127.0.0.1:8000/v1/", "vLLM - this machine, port 8000"),
        new("http://127.0.0.1:11434/v1/", "Ollama's OpenAI-compatible port - this machine")
    ];

    // Only the keys this file reads. Deliberately not CloudVisionSettingsDto: that shape is what
    // the form saves, and reading it here would drag its validation attributes into a path that
    // only wants three strings.
    private sealed record CloudVisionAccount(string? Provider, string? ApiKey, string? BaseUrl);
}
