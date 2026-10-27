using System.Net.Http.Json;
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
            var (body, error) = await FetchTagsAsync(appSettings, httpClientFactory, logger, ct);
            return error ?? Results.Ok(new { options = ParseModels(body!) });
        })
        .RequireAuthorization("AdminOnly");

        // The vision Model box's list: what is installed, plus the recommended vision models that
        // aren't yet, plus how far along a download is. Picking one that isn't installed is enough -
        // the main app's OllamaModelProvisioner pulls it and switches over when it finishes. Its own
        // path rather than a query on the one above because every client builds this URL from the
        // schema, and not all of them carry a query string through untouched.
        app.MapGet("/api/settings/ollama/vision-models", async (
            AppSettingsFile appSettings,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<OllamaModelLog> logger,
            CancellationToken ct) =>
        {
            var (body, error) = await FetchTagsAsync(appSettings, httpClientFactory, logger, ct);
            if (error is not null) return error;
            var status = await FetchDownloadStatusAsync(appSettings, httpClientFactory, configuration, logger, ct);
            return Results.Ok(new { options = VisionOptions(ParseModels(body!), status) });
        })
        .RequireAuthorization("AdminOnly");

        // Whether alerts are getting the full AI, for the setup wizard and the dashboard's nudge
        // banner. A new install starts on the fast detector and only downloads Ollama's model once
        // someone picks it, so "quick" and "downloading" are ordinary states, not faults. AdminOnly:
        // only an admin can act on any of it, and everyone else just gets no banner.
        app.MapGet("/api/system/full-ai", async (
            AppSettingsFile appSettings,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<OllamaModelLog> logger,
            CancellationToken ct) =>
        {
            var routing = await appSettings.GetSectionAsync<VisionRoutingSettingsDto>("VisionRouting", ct)
                          ?? new VisionRoutingSettingsDto();
            var cloud = await appSettings.GetSectionAsync<CloudVisionSettingsDto>("CloudVision", ct);
            var cloudConfigured = cloud is { Enabled: true } && !string.IsNullOrWhiteSpace(cloud.ApiKey);

            if (FullAiWithoutOllama(routing, cloudConfigured) is { } settled)
                return Results.Ok(settled);

            var (body, error) = await FetchTagsAsync(appSettings, httpClientFactory, logger, ct);
            var status = await FetchDownloadStatusAsync(appSettings, httpClientFactory, configuration, logger, ct);
            return Results.Ok(FullAiState(routing.Primary, error is null ? ParseModels(body!) : null, status));
        })
        .RequireAuthorization("AdminOnly");

        // The banner's "use the full AI" button. VISION ROUTING is an advanced-mode section, so
        // sending someone there to change one dropdown would mostly send them to a page without
        // it. Only Primary is written - PUT /vision-routing writes every field of its DTO, defaults
        // included - so thresholds and escalation settings someone already chose stay as they were.
        app.MapPost("/api/system/full-ai/use-ollama", async (AppSettingsFile appSettings, CancellationToken ct) =>
        {
            await appSettings.MergeSectionAsync("VisionRouting", new { Primary = "ollama" }, ct);
            return Results.Ok(new { primary = "ollama" });
        })
        .RequireAuthorization("AdminOnly");
    }

    // state: "quick" (fast detector only), "downloading", "download-failed", "ollama-unreachable"
    // or "full". Percent and Error only mean something while downloading / after a failure.
    public sealed record FullAiStatus(string State, string Primary, string? Model, int Percent, string? Error);

    // The answer when Ollama's state doesn't matter, or null when it does - i.e. when the full
    // analysis comes from Ollama: as the primary, or as the escalation target of the fast detector.
    // Fast detector with escalation to cloud is full AI on every alert that has something in it,
    // so that is "full", not "quick". Mirrors FailoverVisionClient's escalation target rules.
    public static FullAiStatus? FullAiWithoutOllama(VisionRoutingSettingsDto routing, bool cloudConfigured)
    {
        var primary = routing.Primary;
        if (primary == "cloud")
            return new FullAiStatus("full", primary, null, 100, null);
        if (primary != "fastlocal")
            return null;
        if (!routing.EscalationEnabled)
            return new FullAiStatus("quick", primary, null, 0, null);
        var escalatesToCloud = routing.EscalationTarget == "cloud" || (routing.EscalationTarget == "auto" && cloudConfigured);
        return escalatesToCloud ? new FullAiStatus("full", primary, null, 100, null) : null;
    }

    // Public so the decision can be tested without Ollama or the main app. `installed` is null when
    // Ollama couldn't be reached. Between the routing change landing and the main app's first pull
    // update there is a moment where the model is absent but nothing is downloading yet - that is
    // reported as downloading at 0%, since the pull is about to start.
    public static FullAiStatus FullAiState(string primary, List<SettingsFieldOption>? installed, OllamaModelStatus? status)
    {
        var model = status?.Configured;
        if (installed is null)
            return new FullAiStatus("ollama-unreachable", primary, model, 0, null);

        if (status?.Download is { } download)
            return download.Error is null
                ? new FullAiStatus("downloading", primary, download.Model, download.Percent, null)
                : new FullAiStatus("download-failed", primary, download.Model, download.Percent, download.Error);

        if (model is not null && status!.Active is null &&
            !installed.Any(o => OllamaModelProvisioner.IsSameModel(o.Value, model)))
            return new FullAiStatus("downloading", primary, model, 0, null);

        return new FullAiStatus("full", primary, status?.Active ?? model, 100, null);
    }

    // Vision models worth offering on a machine with no GPU, fastest first. Measured on an i7-6700
    // with 22 real snapshots (2026-10-19): accuracy was within a couple of images of each other, so
    // speed is what separates them. Sizes are Ollama's download sizes for these exact tags.
    public static readonly IReadOnlyList<RecommendedModel> RecommendedVisionModels =
    [
        new("gemma4:e2b-it-qat", "4.3 GB", "fastest - recommended"),
        new("gemma4:e4b-it-qat", "6.1 GB", "about 1.7x slower, fewest false alarms"),
        new("gemma3:4b", "3.3 GB", "the previous default, about 2x slower"),
        new("qwen3-vl:4b-instruct", "3.3 GB", "about 3x slower"),
    ];

    public sealed record RecommendedModel(string Name, string Size, string Note);

    // Public so the merge can be tested without Ollama or the main app.
    public static List<SettingsFieldOption> VisionOptions(List<SettingsFieldOption> installed, OllamaModelStatus? status)
    {
        var options = new List<SettingsFieldOption>();
        var download = status?.Download;

        foreach (var model in RecommendedVisionModels)
        {
            var pulled = installed.FirstOrDefault(o => OllamaModelProvisioner.IsSameModel(o.Value, model.Name));
            string label;
            if (download is not null && OllamaModelProvisioner.IsSameModel(model.Name, download.Model))
                label = download.Error is null
                    ? $"{model.Name} - downloading {download.Percent}%, switches over when done"
                    : $"{model.Name} - download failed, retrying every 10 minutes ({download.Error})";
            else if (pulled is not null)
                label = $"{pulled.Label} - {model.Note}";
            else
                label = $"{model.Name} - not downloaded yet, {model.Size} - downloads when you save - {model.Note}";
            options.Add(new SettingsFieldOption(model.Name, label));
        }

        // Then everything else already pulled, in the plain installed-models form. Text-only models
        // are in here too: Ollama's tag list doesn't say which can see images.
        options.AddRange(installed.Where(o =>
            !RecommendedVisionModels.Any(r => OllamaModelProvisioner.IsSameModel(o.Value, r.Name))));

        // A typed name that isn't in either list but is downloading still deserves its progress.
        if (download is not null && !options.Any(o => OllamaModelProvisioner.IsSameModel(o.Value, download.Model)))
            options.Insert(0, new SettingsFieldOption(download.Model, download.Error is null
                ? $"{download.Model} - downloading {download.Percent}%, switches over when done"
                : $"{download.Model} - download failed, retrying every 10 minutes ({download.Error})"));

        return options;
    }

    private static async Task<(string? Body, IResult? Error)> FetchTagsAsync(
        AppSettingsFile appSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaModelLog> logger,
        CancellationToken ct)
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
            return (null, Results.Json(
                new { error = $"Ollama:BaseUrl in appsettings.json is not a valid address ({baseUrl}). Fix it there, or type the model name." },
                statusCode: 502));

        var client = httpClientFactory.CreateClient(nameof(OllamaModelEndpoints));

        try
        {
            using var response = await client.GetAsync(new Uri(root, "api/tags"), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama model list returned {StatusCode}", (int)response.StatusCode);
                return (null, Results.Json(
                    new { error = $"Ollama at {baseUrl} answered {(int)response.StatusCode} instead of its model list. Type the model name instead." },
                    statusCode: 502));
            }

            return (await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   && !ct.IsCancellationRequested)
        {
            // Overwhelmingly "Ollama isn't running" or "it's on another machine that's off",
            // so the message names the address it tried and says what to do instead.
            logger.LogInformation(ex, "Could not read the model list from Ollama at {BaseUrl}", baseUrl);
            return (null, Results.Json(
                new { error = $"Couldn't reach Ollama at {baseUrl} to list its models. Check it's running, or type the model name." },
                statusCode: 502));
        }
    }

    // Asks the main app what its OllamaModelProvisioner is doing. Best effort: if the main app is
    // down or older than the endpoint, the list simply shows no progress. Same addressing and
    // API-key rules as MainAppHubBackupClient.
    private static async Task<OllamaModelStatus?> FetchDownloadStatusAsync(
        AppSettingsFile appSettings,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OllamaModelLog> logger,
        CancellationToken ct)
    {
        var bridge = await appSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
        if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey)) return null;

        var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(OllamaModelEndpoints));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/ollama/model-status");
        request.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<OllamaModelStatus>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                   && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not ask the main app for Ollama download progress");
            return null;
        }
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
