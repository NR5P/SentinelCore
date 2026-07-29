using System.Net.Http.Json;

namespace SentinelCore.Web;

public sealed record ChatQuestionRequest(string Question);

// Forwards a dashboard question into the main app's own POST /api/chat/ask, which answers
// using the same Q&A logic (ChatAnswerService) the Telegram bot already uses - reuses that
// logic rather than duplicating it here, exactly like AlertForwarder reuses the alert pipeline
// instead of re-implementing AI analysis in this project.
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat/ask", async (
            ChatQuestionRequest request,
            IHttpClientFactory httpClientFactory,
            AppSettingsFile mainAppSettings,
            IConfiguration configuration,
            ILogger<ChatQuestionRequest> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return Results.BadRequest(new { error = "A question is required." });

            var bridge = await mainAppSettings.GetSectionAsync<BridgeSettingsDto>("Bridge", ct);
            if (bridge is null || string.IsNullOrWhiteSpace(bridge.ApiKey))
                return Results.Json(new { error = "The main app's API key is not configured." }, statusCode: 503);

            var baseUrl = (configuration["MainAppBaseUrl"] ?? "http://127.0.0.1:5080").TrimEnd('/');
            var client = httpClientFactory.CreateClient(nameof(ChatEndpoints));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat/ask")
            {
                Content = JsonContent.Create(new { question = request.Question })
            };
            httpRequest.Headers.TryAddWithoutValidation("X-Api-Key", bridge.ApiKey);

            try
            {
                using var response = await client.SendAsync(httpRequest, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("Chat question forward failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                    return Results.Json(new { error = "Could not reach the AI just now." }, statusCode: 502);
                }

                var payload = await response.Content.ReadFromJsonAsync<ChatAnswerResponse>(cancellationToken: ct);
                return Results.Ok(new { answer = payload?.Answer ?? "No answer was returned." });
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Could not reach the main app at {BaseUrl} to ask a question", baseUrl);
                return Results.Json(new { error = "Could not reach the main app." }, statusCode: 502);
            }
        }).RequireAuthorization();
    }

    private sealed record ChatAnswerResponse(string? Answer);
}
