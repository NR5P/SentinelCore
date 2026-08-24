using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace SentinelCore;

// "Test cloud vision", behind POST /api/settings/cloud-vision/test, reached from the CLOUD VISION
// settings section on every client via that section's ExtraAction.
//
// Same reasoning as NotificationTestEndpoints, and the same failure it exists to prevent: until
// this button existed, a wrong API key, a model name with a typo in it, an address pointing at
// nothing, or a text-only model chosen by mistake all looked identical from the outside - alerts
// simply arrived without analysis, which reads as a quiet night. The cloud backend is the worst
// place for that particular silence, because it is also the one that costs money when it does
// work, so "is it on?" is not a question anyone should answer by waiting.
//
// Like the notification tests, this deliberately ignores CloudVision:Enabled and every routing
// rule in VisionRouting. It answers exactly one question - do these saved credentials, this
// address and this model produce an analysis? - and it goes straight to CloudVisionClient rather
// than through FailoverVisionClient, because a fallback quietly answering from Ollama would make
// a broken cloud config look fine, which is the opposite of the point.
public static class CloudVisionTestEndpoints
{
    public static void MapCloudVisionTestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/settings/cloud-vision/test", async (
            CloudVisionClient cloudVision,
            IOptionsMonitor<CloudVisionOptions> options,
            ILogger<CloudVisionTestLog> logger,
            CancellationToken ct) =>
        {
            var settings = options.CurrentValue;

            // Checked here rather than left to the provider, because "no key" comes back from the
            // three providers as three different unhelpful strings.
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                return Fail("No API key is saved. Enter one above, save, then test again.");

            if (string.IsNullOrWhiteSpace(settings.Model))
                return Fail("No model is saved. Pick or type one above, save, then test again.");

            var alert = new IncomingAlert(
                Id: Guid.NewGuid(),
                Camera: "Cloud Vision test",
                BlueIrisAlertId: null,
                OccurredAt: DateTimeOffset.UtcNow,
                ReceivedAt: DateTimeOffset.UtcNow,
                Profile: "default",
                ContentType: "image/jpeg",
                OriginalFileName: "cloud-vision-test.jpg",
                ImageBytes: BuildTestImage());

            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                var (analysis, _, model) = await cloudVision.AnalyzeAsync(alert, TestProfile, ct);
                var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

                logger.LogInformation(
                    "Cloud vision test succeeded via {Provider}/{Model} in {ElapsedSeconds:F1}s",
                    settings.Provider, model, elapsed);

                // The summary is echoed back because it is the one part of the reply that proves
                // the model actually looked at the image rather than merely answering. A model
                // that cannot see returns valid, confident, and completely generic prose - the
                // test image is a plain shape on a plain background precisely so the difference
                // is obvious to whoever pressed the button.
                return Results.Ok(new
                {
                    sent = true,
                    detail =
                        $"{ProviderName(settings.Provider)} answered in {elapsed:F1}s using {model}. " +
                        $"It described the test image as: \"{Trim(analysis.Summary)}\" - the image is a " +
                        "single grey rectangle on a white background, so an answer that describes anything " +
                        "else means the model is not really seeing it. Cloud vision is not switched on by " +
                        "this test; use Enabled above and VISION ROUTING to put it to work."
                });
            }
            catch (Exception ex)
            {
                // AnalyzeAsync wraps the provider's own last failure after its retries, and that
                // inner message is the useful one ("model does not exist", "insufficient_quota",
                // "No such host is known"). Paraphrasing it here would only lose it - the same
                // reasoning as NotificationTestProxyEndpoints passing responses straight through.
                var detail = (ex.InnerException ?? ex).Message;

                logger.LogWarning(ex, "Cloud vision test failed for {Provider}/{Model}",
                    settings.Provider, settings.Model);

                return Fail(
                    $"{ProviderName(settings.Provider)} did not return an analysis for model " +
                    $"\"{settings.Model}\": {Trim(detail)}");
            }
        });
    }

    // A test that asks the real questions would spend more tokens and tell you nothing extra -
    // whether the model can see is settled by one question, and the failure modes worth catching
    // (bad key, bad address, bad model, blind model) all show up on the first one.
    private static readonly PromptProfile TestProfile = new()
    {
        SystemPrompt =
            "You are testing a security camera pipeline. Describe the test image literally and briefly.",
        Questions = ["What shape and colour is in this image, and what colour is the background?"]
    };

    // Generated rather than shipped as a file so there is no asset to lose in a publish - the
    // published-bundle path has rearranged relative paths before. Deliberately trivial and
    // unambiguous: a model that is genuinely reading the image says "grey rectangle on white",
    // and one that is only pattern-matching on the prompt says something about a driveway.
    // Pixels are written directly rather than through SixLabors.ImageSharp.Drawing, which is a
    // separate package this project doesn't reference. One filled rectangle isn't worth a new
    // dependency in the alert pipeline's own assembly.
    private static byte[] BuildTestImage()
    {
        var grey = new Rgb24(105, 105, 105);
        using var image = new Image<Rgb24>(320, 240, new Rgb24(255, 255, 255));

        for (var y = 70; y < 170; y++)
            for (var x = 90; x < 230; x++)
                image[x, y] = grey;

        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }

    private static string ProviderName(string provider) => provider switch
    {
        "anthropic" => "Anthropic",
        "google" => "Google",
        _ => "The provider"
    };

    // Provider errors can carry a whole JSON body, and the settings forms show this in a single
    // line of text on a phone as readily as on a monitor.
    private static string Trim(string? text)
    {
        var single = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }

    // 502 and the "error" key, matching NotificationTestEndpoints: the settings are legal, it is
    // something downstream that refused them, and every client reads "error" out of a non-2xx body.
    private static IResult Fail(string detail) =>
        Results.Json(new { sent = false, error = detail }, statusCode: 502);
}

// Marker type only, for a distinctly-named ILogger<T> category.
public sealed class CloudVisionTestLog;
