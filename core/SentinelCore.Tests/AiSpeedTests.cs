using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The AI SPEED card's numbers (AiSpeedStats), the slowdown warning rule (AiSlowdownRule), and the
// dashboard's once-per-12-hours throttle around it (AiSpeedWatch).
public sealed class AiSpeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AnalysisTimingRow Done(string model, double seconds, TimeSpan ago, string camera = "Gate Camera", AnalysisTiming? timing = null)
    {
        var completed = Now - ago;
        return new AnalysisTimingRow(model, camera, "completed", completed - TimeSpan.FromSeconds(seconds), completed,
            timing?.ModelLoadSeconds, timing?.PromptSeconds, timing?.ResponseSeconds);
    }

    private static AnalysisTimingRow Row(string model, string status, TimeSpan ago) =>
        new(model, "Gate Camera", status, Now - ago, null);

    [Fact]
    public void Build_SplitsDayFromWeek_PerModel_AndCountsFailuresWithoutTimes()
    {
        var rows = new List<AnalysisTimingRow>
        {
            Done("gemma4:e4b-it-qat", 100, TimeSpan.FromHours(1)),
            Done("gemma4:e4b-it-qat", 120, TimeSpan.FromHours(2)),
            Done("gemma4:e4b-it-qat", 300, TimeSpan.FromHours(3)),
            Done("gemma4:e4b-it-qat", 80, TimeSpan.FromDays(3)),
            Row("gemma4:e4b-it-qat", "failed", TimeSpan.FromHours(4)),
            Row("gemma4:e4b-it-qat", "superseded", TimeSpan.FromHours(4)),
            Row("gemma4:e4b-it-qat", "queued", TimeSpan.FromMinutes(1)),
            Done("gemma3:4b", 157, TimeSpan.FromDays(2)),
            // Older than the week: ignored entirely.
            Done("gemma3:4b", 999, TimeSpan.FromDays(8)),
        };

        var report = AiSpeedStats.Build(rows, Now, "gemma4:e4b-it-qat", 300);

        Assert.Equal("gemma4:e4b-it-qat", report.CurrentModel);
        Assert.Equal(300, report.RequestTimeoutSeconds);
        Assert.Equal(["gemma4:e4b-it-qat", "gemma3:4b"], report.Models.Select(m => m.Model));

        var current = report.Models[0];
        Assert.Equal(new AiSpeedWindow(3, 1, 173.3, 120, 300, 300), current.Day);
        Assert.Equal(new AiSpeedWindow(4, 1, 150, 110, 300, 300), current.Week);

        var old = report.Models[1];
        Assert.Equal(0, old.Day.Count);
        Assert.Null(old.Day.AverageSeconds);
        Assert.Equal(157, old.Week.SlowestSeconds);
    }

    [Fact]
    public void Build_LatestIsNewestFirst_WithCameraAndOllamasBreakdown()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => Done("gemma4:e4b-it-qat", i * 10, TimeSpan.FromMinutes(i), camera: $"Cam {i}"))
            .Append(Done("gemma4:e4b-it-qat", 95.04, TimeSpan.FromSeconds(10), camera: "Front", timing: new AnalysisTiming(1.26, 80.04, 13.7)))
            .ToList();

        var latest = AiSpeedStats.Build(rows, Now, "gemma4:e4b-it-qat", 300).Latest;

        Assert.Equal(AiSpeedStats.LatestCount, latest.Count);
        Assert.Equal(new AiSpeedSample("gemma4:e4b-it-qat", "Front", Now - TimeSpan.FromSeconds(10), 95, 1.3, 80, 13.7), latest[0]);
        Assert.Equal("Cam 1", latest[1].Camera);
        Assert.Null(latest[1].PromptSeconds);
    }

    [Fact]
    public void MedianAndP90_UseTheUsualDefinitions()
    {
        Assert.Equal(2.5, AiSpeedStats.Median([1, 2, 3, 4]));
        Assert.Equal(3, AiSpeedStats.Median([1, 3, 9]));
        Assert.Equal(9, AiSpeedStats.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.90));
        Assert.Equal(7, AiSpeedStats.Percentile([7], 0.90));
    }

    [Fact]
    public async Task SqliteEventStore_KeepsOllamasBreakdown_AndReturnsRowsSince()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var options = Options.Create(new BridgeOptions { ApiKey = "test-key-1234567890", DataDirectory = tempDir });
            using var store = new SqliteEventStore(options, new HostEnvironment(tempDir));

            var received = Now - TimeSpan.FromMinutes(5);
            var analysed = new AlertResult
            {
                Id = Guid.NewGuid(), Camera = "Gate Camera", OccurredAt = received, ReceivedAt = received,
                CompletedAt = received + TimeSpan.FromSeconds(95), Profile = "default", Model = "gemma4:e4b-it-qat",
                Status = "completed",
                Analysis = new VisionAnalysis { Summary = "A car", Timing = new AnalysisTiming(1.25, 80, 13.75) }
            };
            await store.RecordAsync(analysed, Ct);
            await store.RecordAsync(new AlertResult
            {
                Id = Guid.NewGuid(), Camera = "Gate Camera", OccurredAt = received.AddDays(-9), ReceivedAt = received.AddDays(-9),
                Profile = "default", Model = "gemma3:4b", Status = "failed", Error = "timeout"
            }, Ct);

            var row = Assert.Single(await store.GetAnalysisTimingsAsync(Now - AiSpeedStats.Week, Ct));
            Assert.Equal("gemma4:e4b-it-qat", row.Model);
            Assert.Equal(95, row.Seconds!.Value, 3);
            Assert.Equal((1.25, 80.0, 13.75), (row.ModelLoadSeconds, row.PromptSeconds, row.ResponseSeconds));
            Assert.Equal(95, row.AiSeconds!.Value, 3);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    // A week of ~90 s analyses, then whatever the last five were.
    private static List<AnalysisTimingRow> Week(string model, params double[] lastFive)
    {
        var rows = Enumerable.Range(0, 40)
            .Select(i => Done(model, 85 + i % 10, TimeSpan.FromHours(2 + i * 3)))
            .ToList();
        rows.AddRange(lastFive.Select((s, i) => Done(model, s, TimeSpan.FromMinutes(5 + i * 10))));
        return rows;
    }

    [Fact]
    public void Rule_WarnsWhenTheLastFiveRunMoreThanTwiceTheWeeksMedian()
    {
        var warning = Assert.Single(AiSlowdownRule.Evaluate(Week("gemma4:e4b-it-qat", 200, 210, 190, 90, 220), Now, 2, 0, 300));
        Assert.Equal("slow:gemma4:e4b-it-qat", warning.Key);
        Assert.Contains("2.2x slower", warning.Subject);
        Assert.Contains("median of 200 s over its last 5", warning.Text);
        Assert.Contains("against 90 s normally", warning.Text);
    }

    [Fact]
    public void Rule_IgnoresOneSlowOutlier_AndNormalSpeed()
    {
        // A model reload after Ollama restarted: one slow one, the rest normal.
        Assert.Empty(AiSlowdownRule.Evaluate(Week("m", 500, 90, 88, 92, 91), Now, 2, 0, 600));
        // Slower, but not twice as slow.
        Assert.Empty(AiSlowdownRule.Evaluate(Week("m", 170, 175, 160, 165, 170), Now, 2, 0, 600));
    }

    [Fact]
    public void Rule_NeedsABaseline_AndAModelStillInUse()
    {
        // A model picked this morning: no week to compare against yet.
        var fresh = Enumerable.Range(0, 10).Select(i => Done("new", 300, TimeSpan.FromMinutes(5 + i))).ToList();
        Assert.Empty(AiSlowdownRule.Evaluate(fresh, Now, 2, 0, 600));

        // Switched away from two hours ago: its last five stay slow forever, and say nothing.
        var abandoned = Week("old", 200, 210, 190, 200, 220)
            .Select(r => r with { ReceivedAt = r.ReceivedAt - TimeSpan.FromHours(2), CompletedAt = r.CompletedAt - TimeSpan.FromHours(2) })
            .ToList();
        Assert.Empty(AiSlowdownRule.Evaluate(abandoned, Now, 2, 80, 300));
    }

    [Fact]
    public void Rule_WarnsNearTheTimeout_OnTheAisOwnSeconds()
    {
        // 250 s of a 300 s limit is 83%.
        var near = Week("m", 90, 95, 250, 88, 92);
        var warning = Assert.Single(AiSlowdownRule.Evaluate(near, Now, 2, 80, 300));
        Assert.Equal("timeout:m", warning.Key);
        Assert.Contains("took 250 s, 83% of the 300 s limit", warning.Text);

        // 260 s of waiting, but 200 s of it was queued behind other alerts: the AI itself took 60.
        var queued = Week("m", 90, 95, 88, 92);
        queued.Add(Done("m", 260, TimeSpan.FromMinutes(1), timing: new AnalysisTiming(0.5, 50, 9.5)));
        Assert.Empty(AiSlowdownRule.Evaluate(queued, Now, 2, 80, 300));

        Assert.Empty(AiSlowdownRule.Evaluate(near, Now, 2, 0, 300));
    }

    [Fact]
    public async Task Watch_SendsEachWarningOncePerTwelveHours_AcrossARestart()
    {
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var clock = new ManualTime(Now);
            var notifier = new FakeNotifier();
            var state = Path.Combine(tempDir, "ai-speed-warnings-sent.json");
            var slow = AiSlowdownRule.Evaluate(Week("m", 200, 210, 190, 90, 220), Now, 2, 0, 300);

            await new AiSpeedWatch(notifier, NullLogger<AiSpeedWatch>.Instance, clock, state).CheckAsync(slow, Ct);
            Assert.Single(notifier.Sent);

            // Still slow on every check after it, and after a restart (a new watch reading the file).
            var restarted = new AiSpeedWatch(notifier, NullLogger<AiSpeedWatch>.Instance, clock, state);
            for (var i = 0; i < 10; i++)
            {
                clock.Now += TimeSpan.FromHours(1);
                await restarted.CheckAsync(slow, Ct);
            }
            Assert.Single(notifier.Sent);

            clock.Now += TimeSpan.FromHours(2);
            await restarted.CheckAsync(slow, Ct);
            Assert.Equal(2, notifier.Sent.Count);
        }
        finally
        {
            TempDirectories.Delete(tempDir);
        }
    }

    private sealed class HostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeNotifier : IOwnerNotifier
    {
        public List<(string Subject, string Text)> Sent { get; } = [];

        public Task<bool> SendAsync(string subject, string text, CancellationToken cancellationToken)
        {
            Sent.Add((subject, text));
            return Task.FromResult(true);
        }
    }
}
