using System.Net;
using System.Text.Json;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// "Suggested for you" on Music, against core/docs/MUSIC_AND_PODCASTS.md "### Suggestions": the
/// read (and refresh), "Not interested", downloading as an imported list, and the words.
/// </summary>
public class MusicSuggestionsClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string Path = "/api/music/downloads/suggestions";

    private const string ReadyJson = """
        { "state": "ready", "builtAt": "2026-10-18T22:00:00Z", "message": null,
          "seeds": [ { "artist": "Fleetwood Mac", "reason": "loved" }, { "artist": "Eagles", "reason": "played" } ],
          "albums": [
            { "key": "steely dan|aja", "artist": "Steely Dan", "title": "Aja", "year": 1977,
              "mbid": "rg-1", "artUrl": "https://coverartarchive.org/release-group/rg-1/front-250",
              "because": ["Fleetwood Mac", "Eagles"], "listens": 900000, "queued": false },
            { "key": "carole king|tapestry", "artist": "Carole King", "title": "Tapestry", "year": null,
              "mbid": null, "artUrl": null, "because": null, "listens": null, "queued": true } ] }
        """;

    [Fact]
    public async Task Suggestions_decode_the_full_answer()
    {
        using var server = new FakeServer().OnGet(Path, ReadyJson);
        using var client = ClientFor(server);

        var s = await client.MusicSuggestionsAsync();

        Assert.Equal($"GET {Path}", server.Requests.Single());
        Assert.Equal("ready", s.State);
        Assert.False(s.IsWorking);
        Assert.False(s.IsError);
        Assert.NotNull(s.BuiltAt);
        Assert.Equal(2, s.Seeds.Count);
        Assert.Equal("loved", s.Seeds[0].Reason);
        Assert.Equal("Based on: Fleetwood Mac, Eagles", s.BasedOnLine);
        Assert.Equal(2, s.Albums.Count);
        var aja = s.Albums[0];
        Assert.Equal("steely dan|aja", aja.DisplayKey);
        Assert.Equal("Steely Dan", aja.Artist);
        Assert.Equal(1977, aja.Year);
        Assert.Equal("rg-1", aja.Mbid);
        Assert.StartsWith("https://coverartarchive.org/", aja.ArtUrl);
        Assert.Equal(900000, aja.Listens);
        Assert.False(aja.IsQueued);
        Assert.Equal("Because you like Fleetwood Mac and Eagles", aja.BecauseLine);
        var tapestry = s.Albums[1];
        Assert.True(tapestry.IsQueued);
        Assert.Empty(tapestry.Because);
        Assert.Null(tapestry.BecauseLine);
    }

    [Fact]
    public async Task Refresh_asks_for_a_fresh_build()
    {
        using var server = new FakeServer().OnGet(Path, """{"state":"working","albums":null,"seeds":null}""");
        using var client = ClientFor(server);

        var s = await client.MusicSuggestionsAsync(refresh: true);

        Assert.Equal($"GET {Path}?refresh=true", server.Requests.Single());
        Assert.True(s.IsWorking);
        Assert.Empty(s.Albums);
        Assert.Empty(s.Seeds);
        Assert.Null(s.BasedOnLine);
    }

    [Fact]
    public async Task An_error_keeps_the_albums_and_the_message()
    {
        using var server = new FakeServer().OnGet(Path, """
            { "state": "error", "message": "Couldn't reach ListenBrainz.",
              "albums": [ { "key": "a|b", "artist": "A", "title": "B" } ] }
            """);
        using var client = ClientFor(server);

        var s = await client.MusicSuggestionsAsync();

        Assert.True(s.IsError);
        Assert.Equal("Couldn't reach ListenBrainz.", s.Message);
        Assert.Single(s.Albums);
    }

    [Fact]
    public async Task Downloads_off_surfaces_the_404()
    {
        using var server = new FakeServer().OnGet(Path, """{"error":"Downloads are off."}""", HttpStatusCode.NotFound);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.MusicSuggestionsAsync());

        Assert.Equal(404, error.Status);
    }

    [Fact]
    public async Task Not_interested_in_an_album_posts_artist_and_title()
    {
        using var server = new FakeServer().On("POST", $"{Path}/dismiss", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        await client.DismissMusicSuggestionAsync("Steely Dan", "Aja");

        Assert.Equal($"POST {Path}/dismiss", server.Requests.Single());
        var body = JsonDocument.Parse(server.Bodies.Single()).RootElement;
        Assert.Equal("Steely Dan", body.GetProperty("artist").GetString());
        Assert.Equal("Aja", body.GetProperty("title").GetString());
        Assert.True(body.GetProperty("dismissed").GetBoolean());
    }

    [Fact]
    public async Task Not_interested_in_an_artist_leaves_the_title_out()
    {
        using var server = new FakeServer().On("POST", $"{Path}/dismiss", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        await client.DismissMusicSuggestionAsync("Steely Dan");
        await client.DismissMusicSuggestionAsync("Steely Dan", dismissed: false);

        var first = JsonDocument.Parse(server.Bodies[0]).RootElement;
        Assert.False(first.TryGetProperty("title", out _));
        Assert.True(first.GetProperty("dismissed").GetBoolean());
        Assert.False(JsonDocument.Parse(server.Bodies[1]).RootElement.GetProperty("dismissed").GetBoolean());
    }

    [Fact]
    public async Task Downloading_starts_an_imported_list_named_suggested_for_you()
    {
        using var server = new FakeServer()
            .OnGet(Path, ReadyJson)
            .On("POST", "/api/music/downloads/imports", """{"id":"j1","name":"Suggested for you","counts":{"total":2}}""", HttpStatusCode.Created);
        using var client = ClientFor(server);
        var s = await client.MusicSuggestionsAsync();

        var job = await client.DownloadMusicSuggestionsAsync(s.Albums);

        Assert.Equal("POST /api/music/downloads/imports", server.Requests[1]);
        var body = JsonDocument.Parse(server.Bodies[1]).RootElement;
        Assert.Equal("Suggested for you", body.GetProperty("name").GetString());
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Steely Dan", items[0].GetProperty("artist").GetString());
        Assert.Equal("Aja", items[0].GetProperty("title").GetString());
        Assert.Equal(1977, items[0].GetProperty("year").GetInt32());
        Assert.False(items[1].TryGetProperty("year", out _));
        Assert.Equal("j1", job.Id);
    }

    [Fact]
    public void The_words()
    {
        Assert.Null(MusicSuggestionText.Because(Array.Empty<string>()));
        Assert.Equal("Because you like Eagles", MusicSuggestionText.Because(new[] { "Eagles" }));
        Assert.Equal("Because you like A, B and C", MusicSuggestionText.Because(new[] { "A", " ", "B", "C" }));
        var seeds = Enumerable.Range(1, 7).Select(i => new MusicSuggestionSeed { Artist = $"S{i}" }).ToList();
        Assert.Equal("Based on: S1, S2, S3, S4, S5…", MusicSuggestionText.BasedOn(seeds));
        Assert.Equal("Download all ticked", MusicSuggestionText.DownloadTicked(0));
        Assert.Equal("Download all ticked (3)", MusicSuggestionText.DownloadTicked(3));
        Assert.Equal("steely dan|aja", new MusicSuggestedAlbum { Artist = "Steely Dan", Title = "Aja" }.DisplayKey);
    }
}
