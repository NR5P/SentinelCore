using System.Globalization;

namespace SentinelCore;

// How long the AI takes to read each camera snapshot, per model, out of AlertEvents. Nothing
// watched this until 2026-10-20: the media box went from gemma3:4b (avg 157 s, slowest 506 s) to
// gemma4:e2b (86 s) to gemma4:e4b in one day, and the only way to compare them was grepping the
// journal for "Ollama completed alert". The AI SPEED card on every client and the dashboard's
// AiSpeedWatchWorker both read this.
//
// "Seconds" is CompletedAt - ReceivedAt: what the owner actually waits from the camera's snapshot
// to the verdict, so time spent queued behind other alerts counts too. The Ollama breakdown
// (model load / prompt evaluation / response generation) is the AI's own share of that, where
// known - rows from before the columns existed, and other backends, have none.
public sealed record AnalysisTimingRow(
    string Model,
    string Camera,
    string Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    double? ModelLoadSeconds = null,
    double? PromptSeconds = null,
    double? ResponseSeconds = null)
{
    public double? Seconds => Status == "completed" && CompletedAt is { } completed
        ? Math.Max(0, (completed - ReceivedAt).TotalSeconds)
        : null;

    // The AI's own share, for comparing against Ollama's request timeout - which times the request,
    // not the queue in front of it. The whole wait where the breakdown is missing.
    public double? AiSeconds => Seconds is null ? null
        : ModelLoadSeconds is null && PromptSeconds is null && ResponseSeconds is null ? Seconds
        : (ModelLoadSeconds ?? 0) + (PromptSeconds ?? 0) + (ResponseSeconds ?? 0);
}

// Ollama's own timing for one analysis, from its /api/chat response. Carried on VisionAnalysis
// (not serialized) so the event store can keep it next to the verdict.
public sealed record AnalysisTiming(double ModelLoadSeconds, double PromptSeconds, double ResponseSeconds);

public sealed record AiSpeedWindow(
    int Count,
    int Failed,
    double? AverageSeconds,
    double? MedianSeconds,
    double? P90Seconds,
    double? SlowestSeconds);

public sealed record AiSpeedModel(string Model, AiSpeedWindow Day, AiSpeedWindow Week, DateTimeOffset? LastCompletedAt);

public sealed record AiSpeedSample(
    string Model,
    string Camera,
    DateTimeOffset CompletedAt,
    double Seconds,
    double? ModelLoadSeconds,
    double? PromptSeconds,
    double? ResponseSeconds);

public sealed record AiSpeedReport(
    DateTimeOffset GeneratedAt,
    string? CurrentModel,
    int RequestTimeoutSeconds,
    IReadOnlyList<AiSpeedModel> Models,
    IReadOnlyList<AiSpeedSample> Latest);

public static class AiSpeedStats
{
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);
    public const int LatestCount = 8;

    // rows: everything received in the last week, any status. "superseded" and "queued" rows are
    // ignored (nothing was analysed, or not yet); "failed" ones are counted but have no time.
    public static AiSpeedReport Build(IEnumerable<AnalysisTimingRow> rows, DateTimeOffset now, string? currentModel, int requestTimeoutSeconds)
    {
        var weekStart = now - Week;
        var dayStart = now - Day;
        var relevant = rows
            .Where(r => r.ReceivedAt >= weekStart && r.Status is "completed" or "failed")
            .ToList();

        var models = relevant
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .Select(g => new AiSpeedModel(
                g.Key,
                Window(g.Where(r => r.ReceivedAt >= dayStart)),
                Window(g),
                g.Where(r => r.Seconds is not null).Select(r => r.CompletedAt).Max()))
            // The model in use first, then whichever answered most recently.
            .OrderByDescending(m => string.Equals(m.Model, currentModel, StringComparison.Ordinal))
            .ThenByDescending(m => m.LastCompletedAt ?? DateTimeOffset.MinValue)
            .ToList();

        var latest = relevant
            .Where(r => r.Seconds is not null)
            .OrderByDescending(r => r.CompletedAt)
            .Take(LatestCount)
            .Select(r => new AiSpeedSample(r.Model, r.Camera, r.CompletedAt!.Value, Round(r.Seconds!.Value),
                Round(r.ModelLoadSeconds), Round(r.PromptSeconds), Round(r.ResponseSeconds)))
            .ToList();

        return new AiSpeedReport(now, currentModel, requestTimeoutSeconds, models, latest);
    }

    public static AiSpeedWindow Window(IEnumerable<AnalysisTimingRow> rows)
    {
        var list = rows.ToList();
        var seconds = list.Where(r => r.Seconds is not null).Select(r => r.Seconds!.Value).Order().ToList();
        var failed = list.Count(r => r.Status == "failed");
        if (seconds.Count == 0) return new AiSpeedWindow(0, failed, null, null, null, null);
        return new AiSpeedWindow(
            seconds.Count,
            failed,
            Round(seconds.Average()),
            Round(Median(seconds)),
            Round(Percentile(seconds, 0.90)),
            Round(seconds[^1]));
    }

    // sorted must be ascending and non-empty.
    public static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    // Nearest rank: the smallest value with at least fraction of the samples at or below it, so
    // p90 of ten samples is the 9th and of one sample is that sample.
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static double Round(double value) => Math.Round(value, 1);
    private static double? Round(double? value) => value is { } v ? Math.Round(v, 1) : null;
}

public sealed record AiSlowdownWarning(string Key, string Subject, string Text);

// SELF-MONITORING's AI speed warnings, evaluated by the dashboard's AiSpeedWatchWorker. Two ways
// the AI can be quietly falling behind:
//  - slower than its own normal: the median of a model's last RecentCount analyses more than
//    slowdownFactor times its median over the rest of the week. A median, so one 500 s outlier
//    (a model reload after Ollama restarted) does not count; five in a row slowing down does.
//  - close to giving up: any of those last analyses took timeoutPercent of Ollama's
//    RequestTimeoutSeconds. Past 100% the request is cancelled and the alert fails outright. Timed
//    on the AI's own seconds where Ollama reported them, since the timeout does not see the queue.
// Each model is judged only against itself - gemma3:4b and a 0.3 s fast-local detector have
// nothing to say about each other - and only while it is in use: a model whose newest analysis is
// older than ActiveWithin is not current, and its last five would otherwise sit there forever.
public static class AiSlowdownRule
{
    public const int RecentCount = 5;
    // Below this many older analyses there is no "normal" to be slower than yet - a model picked
    // this morning should not be compared against its own first five.
    public const int MinimumBaseline = 20;
    public static readonly TimeSpan ActiveWithin = TimeSpan.FromHours(1);

    public static IReadOnlyList<AiSlowdownWarning> Evaluate(
        IEnumerable<AnalysisTimingRow> rows,
        DateTimeOffset now,
        double slowdownFactor,
        int timeoutPercent,
        int requestTimeoutSeconds)
    {
        var warnings = new List<AiSlowdownWarning>();
        var weekStart = now - AiSpeedStats.Week;
        foreach (var model in rows
                     .Where(r => r.Seconds is not null && r.ReceivedAt >= weekStart)
                     .GroupBy(r => r.Model, StringComparer.Ordinal))
        {
            var newestFirst = model.OrderByDescending(r => r.CompletedAt).ToList();
            if (newestFirst.Count < RecentCount || now - newestFirst[0].CompletedAt!.Value > ActiveWithin) continue;

            var recentRows = newestFirst.Take(RecentCount).ToList();
            var recent = recentRows.Select(r => r.Seconds!.Value).Order().ToList();
            var recentMedian = AiSpeedStats.Median(recent);

            var baseline = newestFirst.Skip(RecentCount).Select(r => r.Seconds!.Value).Order().ToList();
            if (slowdownFactor > 0 && baseline.Count >= MinimumBaseline)
            {
                var normal = AiSpeedStats.Median(baseline);
                if (normal > 0 && recentMedian > slowdownFactor * normal)
                {
                    warnings.Add(new AiSlowdownWarning(
                        $"slow:{model.Key}",
                        $"Camera AI is running {recentMedian / normal:0.#}x slower than normal",
                        $"{model.Key} has taken a median of {Seconds(recentMedian)} over its last {RecentCount} snapshots, " +
                        $"against {Seconds(normal)} normally this week. Alerts are reaching you that much later. " +
                        "Usual causes: the server is short of memory or busy with something else (a build, a transcode), " +
                        "another model was loaded alongside it, or the alert queue is backing up. " +
                        "The AI Speed card in Settings shows the latest timings."));
                }
            }

            if (timeoutPercent > 0 && requestTimeoutSeconds > 0)
            {
                var limit = requestTimeoutSeconds * timeoutPercent / 100.0;
                var slowest = recentRows.Max(r => r.AiSeconds!.Value);
                if (slowest >= limit)
                {
                    warnings.Add(new AiSlowdownWarning(
                        $"timeout:{model.Key}",
                        "Camera AI is close to timing out",
                        $"One of {model.Key}'s last {RecentCount} snapshots took {Seconds(slowest)}, " +
                        $"{slowest * 100 / requestTimeoutSeconds:0}% of the {requestTimeoutSeconds} s limit " +
                        "(Ollama:RequestTimeoutSeconds in appsettings.json). Past the limit the analysis is abandoned and that alert fails. " +
                        "A smaller or faster model, or more memory for the server, gives it room again."));
                }
            }
        }
        return warnings;
    }

    private static string Seconds(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " s";
}
