using System.Net;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Lightning on the radar map: /api/weather/lightning and the keep/append/replace rules ported from
/// the dashboard's weather.js loadLightning.
/// </summary>
public sealed class WeatherLightningTests
{
    private const long Origin = 1792009348923;
    private static readonly DateTimeOffset OriginTime = DateTimeOffset.FromUnixTimeMilliseconds(Origin);

    private static SentinelClient ClientFor(FakeServer server) => new(() => server.BaseUrl, NullSentinelLog.Instance);

    private static WeatherLightning Answer(bool incremental, long sequence, string state, params double[][] flashes) => new()
    {
        State = state,
        Epoch = "66ecec5f",
        Sequence = sequence,
        Incremental = incremental,
        Origin = Origin,
        Flashes = flashes.ToList(),
    };

    [Fact]
    public async Task Lightning_decodes_and_asks_for_the_box()
    {
        using var server = new FakeServer().OnGet("/api/weather/lightning", """
            { "state": "ready", "epoch": "66ecec5f", "sequence": 274, "incremental": false,
              "coverageStart": "2026-10-14T17:00:00+00:00", "coverageEnd": "2026-10-14T18:35:00+00:00",
              "origin": 1792009348923, "truncated": false,
              "flashes": [[41.25, -96.01, 12.5, 340.2], [41.3, -95.9, 70, 1820]] }
            """);
        using var client = ClientFor(server);

        var answer = await client.WeatherLightningAsync(-98.5, 39.25, -93.5, 43.25);

        Assert.NotNull(answer);
        Assert.True(answer!.IsReady);
        Assert.Equal("66ecec5f", answer.Epoch);
        Assert.Equal(274, answer.Sequence);
        Assert.False(answer.Incremental);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 18, 35, 0, TimeSpan.Zero), answer.CoverageEnd);
        Assert.Contains("GET /api/weather/lightning?west=-98.5&south=39.25&east=-93.5&north=43.25", server.Requests);

        var flashes = answer.Decode().ToList();
        Assert.Equal(2, flashes.Count);
        Assert.Equal(41.25, flashes[0].Latitude);
        Assert.Equal(-96.01, flashes[0].Longitude);
        Assert.Equal(OriginTime.AddMilliseconds(12500), flashes[0].Time);
        Assert.Equal(340.2, flashes[0].EnergyFemtojoules);
        Assert.Equal(OriginTime.AddSeconds(70), flashes[1].Time);
    }

    [Fact]
    public async Task Lightning_sends_epoch_and_after_for_only_new_flashes()
    {
        using var server = new FakeServer().OnGet("/api/weather/lightning", """
            { "state": "ready", "epoch": "66ecec5f", "sequence": 275, "incremental": true, "origin": 1792009348923, "flashes": [] }
            """);
        using var client = ClientFor(server);

        var answer = await client.WeatherLightningAsync(-97, 40, -95, 42, "66ecec5f", 274);

        Assert.True(answer!.Incremental);
        Assert.Contains("GET /api/weather/lightning?west=-97&south=40&east=-95&north=42&epoch=66ecec5f&after=274", server.Requests);
    }

    [Fact]
    public async Task Lightning_is_null_from_a_server_without_it()
    {
        using var server = new FakeServer().OnGet("/api/weather/elsewhere", "{}");
        using var client = ClientFor(server);

        Assert.Null(await client.WeatherLightningAsync(-97, 40, -95, 42));
    }

    [Fact]
    public void History_appends_incremental_answers_and_replaces_full_ones()
    {
        var now = OriginTime.AddMinutes(10);
        var history = new LightningHistory();

        history.Apply(Answer(false, 1, "ready", [41, -96, 10, 100], [41.1, -96.1, 20, 200]), now);
        Assert.Equal(2, history.Flashes.Count);
        Assert.Equal(1, history.Sequence);

        history.Apply(Answer(true, 2, "ready", [41.2, -96.2, 30, 300]), now);
        Assert.Equal(3, history.Flashes.Count);
        Assert.Equal(2, history.Sequence);
        Assert.Equal("66ecec5f", history.Epoch);

        history.Apply(Answer(false, 3, "ready", [42, -95, 40, 400]), now);
        var only = Assert.Single(history.Flashes);
        Assert.Equal(42, only.Latitude);
    }

    [Fact]
    public void History_keeps_95_minutes_oldest_first()
    {
        var history = new LightningHistory();
        var now = OriginTime.AddMinutes(100);

        // 0 s is 100 min old (dropped), 360 s is 94 min old (kept); out of order on purpose.
        history.Apply(Answer(false, 1, "ready", [41, -96, 900, 1], [41, -96, 0, 1], [41, -96, 360, 1]), now);

        Assert.Equal(2, history.Flashes.Count);
        Assert.Equal(OriginTime.AddSeconds(360), history.Flashes[0].Time);
        Assert.Equal(OriginTime.AddSeconds(900), history.Flashes[1].Time);
    }

    [Fact]
    public void History_polls_fast_until_ready()
    {
        var history = new LightningHistory();
        Assert.Equal(TimeSpan.FromSeconds(4), history.NextPoll);

        history.Apply(Answer(false, 1, "loading"), OriginTime);
        Assert.Equal(TimeSpan.FromSeconds(4), history.NextPoll);

        history.Apply(Answer(true, 2, "ready"), OriginTime);
        Assert.Equal(TimeSpan.FromSeconds(30), history.NextPoll);

        history.Clear();
        Assert.Null(history.Epoch);
        Assert.Empty(history.Flashes);
    }
}
