using System.Text.Json;
using SentinelCore;

namespace SentinelCore.Web;

// Answers "which models are actually pulled on the machine running Ollama?", so the Model boxes
// in Settings can offer a list to pick from instead of asking someone to remember whether they
// pulled gemma3:4b or gemma3:4b-it-qat. Typing a name that isn't in the list still works
// everywhere - see SettingsOptionsSourceSchema for the contract this endpoint fills.
//
// The tag it needs is Ollama:BaseUrl, which is not itself an editable setting (it's read once at
// startup by the main app's HttpClient), so this reads it straight out of the same
// appsettings.json the rest of this page writes to rather than from this project's own config.
public sealed class OllamaModelLog;

public static class OllamaModelEndpoints
{
    public static void MapOllamaModelEndpoints(this WebApplication app)
    {
        // AdminOnly to match the rest of /api/settings, of which this is a helper. It also names
        // every model on the box, which is a small piece of "what is this machine running".
        app.MapGet("/api/settings/ollama/models", async (
            AppSettingsFile appSettings,
            IHttpClientFactory httpClientFactory,
            ILogger<OllamaModelLog> logger,
            CancellationToken ct) =>
        {
            var configured = await appSettings.GetSectionAsync<OllamaAddress>("Ollama", ct);
            var baseUrl = string.IsNullOrWhiteSpace(configured?.BaseUrl)
                ? new OllamaOptions().BaseUrl
                : configured.BaseUrl.Trim();

            // Trailing slash or the relative "api/tags" below would replace the last path segment
            // rather than extend it - which matters for anyone running Ollama behind a reverse
            // proxy on a subpath.
            if (!baseUrl.EndsWith('/')) baseUrl += "/";

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root))
                return Results.Json(
                    new { error = $"Ollama:BaseUrl in appsettings.json is not a valid address ({baseUrl}). Fix it there, or type the model name." },
                    statusCode: 502);

            var client = httpClientFactory.CreateClient(nameof(OllamaModelEndpoints));

            try
            {
                using var response = await client.GetAsync(new Uri(root, "api/tags"), ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Ollama model list returned {StatusCode}", (int)response.StatusCode);
                    return Results.Json(
                        new { error = $"Ollama at {baseUrl} answered {(int)response.StatusCode} instead of its model list. Type the model name instead." },
                        statusCode: 502);
                }

                return Results.Ok(new { options = ParseModels(await response.Content.ReadAsStringAsync(ct)) });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       && !ct.IsCancellationRequested)
            {
                // Overwhelmingly "Ollama isn't running" or "it's on another machine that's off",
                // so the message names the address it tried and says what to do instead.
                logger.LogInformation(ex, "Could not read the model list from Ollama at {BaseUrl}", baseUrl);
                return Results.Json(
                    new { error = $"Couldn't reach Ollama at {baseUrl} to list its models. Check it's running, or type the model name." },
                    statusCode: 502);
            }
        })
        .RequireAuthorization("AdminOnly");
    }

    // Ollama already reports fully-qualified names ("gemma3:4b"), which is exactly the string the
    // setting has to hold, so the value is the name untouched. The label adds the size, because
    // that is what tells a 4B apart from a 27B when both are called gemma3 - and size is what
    // decides whether a machine can run the thing at all.
    //
    // Reads the body rather than a deserialized shape, and is public, so the mapping can be tested
    // against a real /api/tags payload without a running Ollama - the ordering and the "skip an
    // entry with no name" rule are the parts worth holding to account. Anything else in the
    // response (digest, family, quantization) is ignored: nothing shows it.
    public static List<SettingsFieldOption> ParseModels(string body)
    {
        var options = new List<SettingsFieldOption>();

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("name", out var nameElement) ||
                nameElement.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name))
                continue;

            var size = model.TryGetProperty("size", out var sizeElement)
                       && sizeElement.TryGetInt64(out var bytes)
                ? bytes
                : 0;

            options.Add(new SettingsFieldOption(
                name,
                size > 0 ? $"{name} - {Gigabytes(size)}" : name));
        }

        // Ollama lists most-recently-modified first, which is meaningless in a dropdown someone is
        // scanning for a name.
        options.Sort((left, right) => string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));
        return options;
    }

    private static string Gigabytes(long bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:0.#} GB";

    // The one key this reads out of the main app's Ollama section. Deliberately not
    // OllamaSettingsDto: that is the editable-in-Settings shape, and BaseUrl is not in it.
    private sealed record OllamaAddress(string? BaseUrl);
}
