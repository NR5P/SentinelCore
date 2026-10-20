using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The AI SPEED card: GET /api/system/ai-speed (AiSpeedStats on the server) decodes, picks the
/// model in use, and reads the way the other clients read it.
/// </summary>
public class AiSpeedClientTests
{
    private const string Path = "/api/system/ai-speed";

    private const string ReportJson = """
        { "generatedAt": "2026-10-20T18:00:00+00:00", "currentModel": "gemma4:e4b-it-qat", "requestTimeoutSeconds": 300,
          "models": [
            { "model": "gemma4:e4b-it-qat",
              "day": { "count": 3, "failed": 1, "averageSeconds": 173.3, "medianSeconds": 120, "p90Seconds": 300, "slowestSeconds": 300 },
              "week": { "count": 4, "failed": 1, "averageSeconds": 150, "medianSeconds": 110, "p90Seconds": 300, "slowestSeconds": 300 },
              "lastCompletedAt": "2026-10-20T17:00:00+00:00" },
            { "model": "gemma3:4b",
              "day": { "count": 0, "failed": 0, "averageSeconds": null, "medianSeconds": null, "p90Seconds": null, "slowestSeconds": null },
              "week": { "count": 1, "failed": 0, "averageSeconds": 157, "medianSeconds": 157, "p90Seconds": 157, "slowestSeconds": 157 },
              "lastCompletedAt": "2026-10-18T18:00:00+00:00" } ],
          "latest": [
            { "model": "gemma4:e4b-it-qat", "camera": "Front", "completedAt": "2026-10-20T17:59:50+00:00", "seconds": 95,
              "modelLoadSeconds": 1.3, "promptSeconds": 80, "responseSeconds": 13.7 },
            { "model": "gemma3:4b", "camera": "Gate Camera", "completedAt": "2026-10-18T18:00:00+00:00", "seconds": 157,
              "modelLoadSeconds": null, "promptSeconds": null, "responseSeconds": null } ] }
        """;

    [Fact]
    public async Task Report_decodes_and_picks_the_model_in_use()
    {
        using var server = new FakeServer().OnGet(Path, ReportJson);
        using var client = new SentinelClient(() => server.BaseUrl, NullSentinelLog.Instance);

        var report = await client.AiSpeedAsync();

        Assert.Equal($"GET {Path}", server.Requests.Single());
        Assert.Equal(300, report.RequestTimeoutSeconds);
        Assert.Equal("gemma4:e4b-it-qat", report.Current?.Model);
        Assert.Equal(173.3, report.Current?.Day.AverageSeconds);
        Assert.Equal(1, report.Current?.Day.Failed);
        Assert.Null(report.Models[1].Day.SlowestSeconds);
        Assert.Equal(2, report.Latest.Count);
        Assert.Equal("Front", report.Latest[0].Camera);
        Assert.Equal(80, report.Latest[0].PromptSeconds);
        Assert.Null(report.Latest[1].PromptSeconds);
    }

    [Fact]
    public void Words_match_the_other_clients()
    {
        Assert.Equal("–", AiSpeedReport.FormatSeconds(null));
        Assert.Equal("157 s", AiSpeedReport.FormatSeconds(156.8));
        Assert.Equal(9.5.ToString("0.0") + " s", AiSpeedReport.FormatSeconds(9.5));

        var withBreakdown = new AiSpeedSample { Model = "m", PromptSeconds = 80, ModelLoadSeconds = 1.3, ResponseSeconds = 13.7 };
        Assert.StartsWith("5:59 PM · load ", AiSpeedReport.Describe(withBreakdown, "5:59 PM"));
        Assert.Contains("reading 80.0 s".Replace("80.0", 80.0.ToString("0.0")), AiSpeedReport.Describe(withBreakdown, "5:59 PM"));
        Assert.Equal("6:00 PM · gemma3:4b", AiSpeedReport.Describe(new AiSpeedSample { Model = "gemma3:4b" }, "6:00 PM"));
    }

    [Fact]
    public void Current_falls_back_to_the_first_model()
    {
        var report = new AiSpeedReport { CurrentModel = null, Models = [new AiSpeedModel { Model = "x" }] };
        Assert.Equal("x", report.Current?.Model);
        Assert.Null(new AiSpeedReport().Current);
    }
}
