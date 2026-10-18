using System.Net;
using System.Text.Json;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// "Import a list" on Music and Movies, against the contract and sample bodies in
/// core/docs/MOVIE_DOWNLOADS.md "Import a list (Music and Movies)": every route on both bases, the
/// full objects, minimal ones, and the words the dialog shows (the web's media-import.js).
/// </summary>
public class MediaImportsClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    private const string MusicPreviewJson = """
        { "kind": "music", "sources": ["Exportify"], "tracks": 312, "noAlbum": 2, "shows": 0, "unreadable": 1,
          "problems": ["notes.csv: no artist/album or title column."],
          "albums": [
            { "artist": "Fleetwood Mac", "title": "Rumours", "year": 1977, "trackCount": 4,
              "tracks": ["Dreams", "Go Your Own Way"], "single": false, "inLibrary": false },
            { "artist": "Lorde", "title": "Royals", "year": 2013, "trackCount": 1,
              "tracks": ["Royals"], "single": true, "inLibrary": false },
            { "artist": "Adele", "title": "25", "year": 2015, "trackCount": 2,
              "tracks": null, "single": false, "inLibrary": true } ],
          "movies": [] }
        """;

    private const string MoviePreviewJson = """
        { "kind": "movies", "sources": ["Letterboxd"], "tracks": 0, "noAlbum": 0, "shows": 1, "unreadable": 0,
          "problems": [], "albums": [],
          "movies": [ { "title": "The Matrix", "year": 1999, "imdbId": "tt0133093", "inLibrary": false },
                      { "title": "Dune", "year": 2021, "imdbId": null, "inLibrary": true } ] }
        """;

    private const string JobJson = """
        { "id": "5f0c2a8e-1d2b-4c1e-9a77-0b6c1f3e2d10", "kind": "music", "name": "Liked Songs", "requestedBy": "pat",
          "createdAt": "2026-10-17T10:00:00Z", "paused": false, "message": "Waiting to try again: the VPN is down.",
          "finished": false,
          "counts": { "total": 5, "waiting": 1, "downloading": 1, "done": 1, "inLibrary": 1, "notFound": 1, "failed": 0, "canceled": 0 },
          "items": [
            { "id": "i1", "artist": "Fleetwood Mac", "title": "Rumours", "year": 1977, "trackCount": 4,
              "state": "downloading", "message": null, "progress": 0.425, "downloadId": "d1",
              "releaseTitle": "Fleetwood Mac - Rumours (1977) [FLAC]" },
            { "id": "i2", "artist": "Lorde", "title": "Royals", "year": 2013, "trackCount": 1,
              "state": "notFound", "message": "Nobody is sharing it.", "progress": null, "downloadId": null, "releaseTitle": null },
            { "id": "i3", "artist": "Adele", "title": "25", "year": 2015, "trackCount": 2,
              "state": "inLibrary", "message": null, "progress": null, "downloadId": null, "releaseTitle": null } ] }
        """;

    private const string JobId = "5f0c2a8e-1d2b-4c1e-9a77-0b6c1f3e2d10";

    // ---------------------------------------------------------------- Preview

    [Fact]
    public async Task Preview_posts_the_files_as_text_and_decodes_the_albums()
    {
        using var server = new FakeServer().On("POST", "/api/music/downloads/import/preview", MusicPreviewJson);
        using var client = ClientFor(server);

        var preview = await client.PreviewMediaImportAsync(MediaImportKind.Music, new[]
        {
            new MediaImportFile { Name = "Liked_Songs.csv", Text = "Artist Name(s),Album Name,Track Name\nFleetwood Mac,Rumours,Dreams\n" },
            new MediaImportFile { Name = "notes.csv", Text = "x" },
        });

        Assert.Equal("POST /api/music/downloads/import/preview", server.Requests.Single());
        using (var body = JsonDocument.Parse(server.Bodies.Single()))
        {
            var files = body.RootElement.GetProperty("files");
            Assert.Equal(2, files.GetArrayLength());
            Assert.Equal("Liked_Songs.csv", files[0].GetProperty("name").GetString());
            Assert.StartsWith("Artist Name(s),Album Name", files[0].GetProperty("text").GetString());
        }

        Assert.Equal("music", preview.Kind);
        Assert.Equal(new[] { "Exportify" }, preview.Sources);
        Assert.Equal(312, preview.Tracks);
        Assert.Equal(2, preview.NoAlbum);
        Assert.Equal(1, preview.Unreadable);
        Assert.Single(preview.Problems);
        Assert.Equal(3, preview.Albums.Count);
        Assert.Equal("Rumours", preview.Albums[0].Title);
        Assert.Equal(2, preview.Albums[0].Tracks.Count);
        Assert.Empty(preview.Albums[2].Tracks); // null survives as empty
        Assert.True(preview.Albums[1].Single);
        Assert.True(preview.Albums[2].InLibrary);

        var choices = preview.Choices(MediaImportKind.Music);
        Assert.Equal(3, choices.Count);
        Assert.Equal("Fleetwood Mac\u0001Rumours", choices[0].Key);
        Assert.True(choices[0].Matches("fleetwood"));
        Assert.True(choices[0].Matches("1977"));
        Assert.False(choices[0].Matches("lorde"));
    }

    [Fact]
    public async Task Movie_preview_goes_to_the_movies_base_and_decodes_films()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/import/preview", MoviePreviewJson);
        using var client = ClientFor(server);

        var preview = await client.PreviewMediaImportAsync(MediaImportKind.Movies, new[] { new MediaImportFile { Name = "watchlist.csv", Text = "Name,Year\nThe Matrix,1999" } });

        Assert.Equal("POST /api/media/downloads/import/preview", server.Requests.Single());
        Assert.Equal(2, preview.Movies.Count);
        Assert.Equal("tt0133093", preview.Movies[0].ImdbId);
        var choices = preview.Choices(MediaImportKind.Movies);
        Assert.Null(choices[0].Artist);
        Assert.Equal("The Matrix\u00011999", choices[0].Key);
        Assert.True(choices[1].InLibrary);
    }

    [Fact]
    public async Task A_minimal_preview_decodes_with_empty_lists()
    {
        using var server = new FakeServer().On("POST", "/api/music/downloads/import/preview", """{"sources":null,"albums":null}""");
        using var client = ClientFor(server);

        var preview = await client.PreviewMediaImportAsync(MediaImportKind.Music, new[] { new MediaImportFile { Name = "a.csv", Text = "x" } });

        Assert.Empty(preview.Sources);
        Assert.Empty(preview.Albums);
        Assert.Empty(preview.Movies);
        Assert.Empty(preview.Problems);
        Assert.Empty(preview.Choices(MediaImportKind.Music));
    }

    [Fact]
    public async Task A_bad_file_surfaces_the_servers_sentence()
    {
        using var server = new FakeServer().On("POST", "/api/media/downloads/import/preview", """{"error":"Choose a file to import."}""", HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() =>
            client.PreviewMediaImportAsync(MediaImportKind.Movies, new[] { new MediaImportFile { Name = "a.csv", Text = "" } }));

        Assert.Equal(400, error.Status);
        Assert.Equal("Choose a file to import.", error.Message);
    }

    // ---------------------------------------------------------------- Start

    [Fact]
    public async Task Start_posts_the_name_and_chosen_items_and_decodes_the_job()
    {
        using var server = new FakeServer().On("POST", "/api/music/downloads/imports", JobJson, HttpStatusCode.Created);
        using var client = ClientFor(server);
        var choices = new[]
        {
            new MediaImportChoice("Fleetwood Mac", "Rumours", 1977, 4, false, false),
            new MediaImportChoice(null, "The Matrix", null, 0, false, false),
        };

        var job = await client.StartMediaImportAsync(MediaImportKind.Music, new MediaImportStartInput
        {
            Name = "Liked Songs",
            Items = choices.Select(c => c.ToStartItem()).ToList(),
        });

        Assert.Equal("POST /api/music/downloads/imports", server.Requests.Single());
        using (var body = JsonDocument.Parse(server.Bodies.Single()))
        {
            var root = body.RootElement;
            Assert.Equal("Liked Songs", root.GetProperty("name").GetString());
            Assert.False(root.TryGetProperty("libraryId", out _)); // null is left out
            var items = root.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            Assert.Equal("Fleetwood Mac", items[0].GetProperty("artist").GetString());
            Assert.Equal("Rumours", items[0].GetProperty("title").GetString());
            Assert.Equal(1977, items[0].GetProperty("year").GetInt32());
            Assert.Equal(4, items[0].GetProperty("trackCount").GetInt32());
            Assert.False(items[1].TryGetProperty("artist", out _));
            Assert.Equal(0, items[1].GetProperty("trackCount").GetInt32());
        }

        Assert.Equal(JobId, job.Id);
        Assert.Equal("Liked Songs", job.Name);
        Assert.Equal("pat", job.RequestedBy);
        Assert.False(job.IsPaused);
        Assert.False(job.IsFinished);
        Assert.True(job.IsBusy);
        Assert.True(job.CanPause);
        Assert.True(job.CanRetry);
        Assert.Equal("Waiting to try again: the VPN is down.", job.Message);
        var counts = job.CountsOrEmpty;
        Assert.Equal(5, counts.Total);
        Assert.Equal(3, counts.Settled);
        Assert.Equal(0.6, counts.Fraction, 3);
        Assert.Equal(3, job.Items.Count);
        Assert.Equal("downloading", job.Items[0].State);
        Assert.Equal(0.425, job.Items[0].Progress);
        Assert.Equal("d1", job.Items[0].DownloadId);
        Assert.False(job.Items[0].CanLeaveOut);
        Assert.True(job.Items[1].CanLeaveOut);
    }

    // ---------------------------------------------------------------- List and one

    [Fact]
    public async Task List_decodes_the_jobs_without_items()
    {
        using var server = new FakeServer().OnGet("/api/media/downloads/imports", """
            { "imports": [ { "id": "a", "kind": "movies", "name": "Watchlist", "paused": true, "finished": false,
                             "counts": { "total": 2, "waiting": 2 }, "items": null },
                           { "id": "b", "name": "Old", "finished": true } ] }
            """);
        using var client = ClientFor(server);

        var jobs = await client.MediaImportsAsync(MediaImportKind.Movies);

        Assert.Equal("GET /api/media/downloads/imports", server.Requests.Single());
        Assert.Equal(2, jobs.Count);
        Assert.True(jobs[0].IsPaused);
        Assert.True(jobs[0].CanPause);
        Assert.False(jobs[0].IsBusy);
        Assert.False(jobs[0].CanRetry);
        Assert.Empty(jobs[0].Items);
        Assert.Equal(2, jobs[0].CountsOrEmpty.Waiting);
        // No counts at all: an empty list, counted as done.
        Assert.True(jobs[1].IsFinished);
        Assert.False(jobs[1].CanPause);
        Assert.Equal(0, jobs[1].CountsOrEmpty.Total);
        Assert.Equal(1, jobs[1].CountsOrEmpty.Fraction);
    }

    [Fact]
    public async Task An_empty_list_answer_is_no_lists()
    {
        using var server = new FakeServer().OnGet("/api/music/downloads/imports", "{}");
        using var client = ClientFor(server);

        Assert.Empty(await client.MediaImportsAsync(MediaImportKind.Music));
    }

    [Fact]
    public async Task One_list_is_fetched_by_id()
    {
        using var server = new FakeServer().OnGet($"/api/music/downloads/imports/{JobId}", JobJson);
        using var client = ClientFor(server);

        var job = await client.MediaImportAsync(MediaImportKind.Music, JobId);

        Assert.Equal($"GET /api/music/downloads/imports/{JobId}", server.Requests.Single());
        Assert.Equal(3, job.Items.Count);
    }

    // ---------------------------------------------------------------- Changes

    [Fact]
    public async Task Pause_and_resume_post_the_flag()
    {
        using var server = new FakeServer().On("POST", $"/api/media/downloads/imports/{JobId}/pause", JobJson);
        using var client = ClientFor(server);

        await client.PauseMediaImportAsync(MediaImportKind.Movies, JobId, paused: true);
        await client.PauseMediaImportAsync(MediaImportKind.Movies, JobId, paused: false);

        Assert.Equal(new[] { $"POST /api/media/downloads/imports/{JobId}/pause", $"POST /api/media/downloads/imports/{JobId}/pause" }, server.Requests);
        Assert.True(JsonDocument.Parse(server.Bodies[0]).RootElement.GetProperty("paused").GetBoolean());
        Assert.False(JsonDocument.Parse(server.Bodies[1]).RootElement.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Retry_posts_to_the_retry_route()
    {
        using var server = new FakeServer().On("POST", $"/api/music/downloads/imports/{JobId}/retry", JobJson);
        using var client = ClientFor(server);

        var job = await client.RetryMediaImportAsync(MediaImportKind.Music, JobId);

        Assert.Equal($"POST /api/music/downloads/imports/{JobId}/retry", server.Requests.Single());
        Assert.Equal(JobId, job.Id);
    }

    [Fact]
    public async Task Leaving_one_out_deletes_the_item_and_returns_the_list()
    {
        using var server = new FakeServer().On("DELETE", $"/api/music/downloads/imports/{JobId}/items/i2", JobJson);
        using var client = ClientFor(server);

        var job = await client.LeaveOutMediaImportItemAsync(MediaImportKind.Music, JobId, "i2");

        Assert.Equal($"DELETE /api/music/downloads/imports/{JobId}/items/i2", server.Requests.Single());
        Assert.Equal(3, job.Items.Count);
    }

    [Fact]
    public async Task Delete_removes_the_list_and_accepts_204()
    {
        using var server = new FakeServer().On("DELETE", $"/api/media/downloads/imports/{JobId}", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        await client.DeleteMediaImportAsync(MediaImportKind.Movies, JobId);

        Assert.Equal($"DELETE /api/media/downloads/imports/{JobId}", server.Requests.Single());
    }

    [Fact]
    public async Task Someone_elses_list_surfaces_the_403_sentence()
    {
        using var server = new FakeServer().On("DELETE", $"/api/music/downloads/imports/{JobId}",
            """{"error":"Only the person who imported this list, or an admin, can change it."}""", HttpStatusCode.Forbidden);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.DeleteMediaImportAsync(MediaImportKind.Music, JobId));

        Assert.Equal(403, error.Status);
        Assert.Equal("Only the person who imported this list, or an admin, can change it.", error.Message);
    }

    // ---------------------------------------------------------------- Words

    [Fact]
    public void Music_preview_words_match_the_web()
    {
        var preview = JsonSerializer.Deserialize<MediaImportPreview>(MusicPreviewJson, SentinelClient.JsonOptions)!;

        Assert.Equal(new[] { "From Exportify", "312 songs → 3 albums", "1 already in the library" }, MediaImportText.Facts(MediaImportKind.Music, preview));
        Assert.Equal(new[]
        {
            "2 songs had no album, so they were left out.",
            "1 row could not be read (podcast episodes, local files, blank titles).",
            "notes.csv: no artist/album or title column.",
            "1 single: one-song releases are rarely shared on their own, so expect some of these to come back not found.",
        }, MediaImportText.Notes(MediaImportKind.Music, preview));
        Assert.Equal("download 1 album", MediaImportText.StartLabel(MediaImportKind.Music, 1));
        Assert.Equal("download 12 albums", MediaImportText.StartLabel(MediaImportKind.Music, 12));
    }

    [Fact]
    public void Movie_preview_words_match_the_web()
    {
        var preview = JsonSerializer.Deserialize<MediaImportPreview>(MoviePreviewJson, SentinelClient.JsonOptions)!;

        Assert.Equal(new[] { "From Letterboxd", "2 movies", "1 already in the library" }, MediaImportText.Facts(MediaImportKind.Movies, preview));
        Assert.Equal(new[] { "1 TV show left out: add it with + Add to pick seasons." }, MediaImportText.Notes(MediaImportKind.Movies, preview));
        Assert.Equal("Working through 2 movies. Watch it under Downloads.", MediaImportText.Started(MediaImportKind.Movies, 2));
    }

    [Fact]
    public void The_default_name_comes_from_the_file()
    {
        Assert.Equal("Liked Songs", MediaImportText.DefaultName(MediaImportKind.Music, new[] { @"C:\Users\pat\Downloads\Liked_Songs.csv" }));
        Assert.Equal("watchlist", MediaImportText.DefaultName(MediaImportKind.Movies, new[] { "watchlist.CSV" }));
        Assert.Equal("Music import", MediaImportText.DefaultName(MediaImportKind.Music, new[] { "a.json", "b.json" }));
        Assert.Equal("Movies import", MediaImportText.DefaultName(MediaImportKind.Movies, new[] { "a.json", "b.json" }));
    }

    [Fact]
    public void Job_words_match_the_web()
    {
        var job = JsonSerializer.Deserialize<MediaImportJob>(JobJson, SentinelClient.JsonOptions)!;

        Assert.Equal("working", MediaImportText.Status(job));
        Assert.Equal(new[] { "1 downloaded", "1 downloading", "1 to search", "1 already had", "1 not found" }, MediaImportText.Progress(job.CountsOrEmpty));
        Assert.Equal("43%", MediaImportText.StateWord(job.Items[0]));
        Assert.Equal("not found", MediaImportText.StateWord(job.Items[1]));
        Assert.Equal("already had it", MediaImportText.StateWord(job.Items[2]));
        Assert.Equal("left out", MediaImportText.StateWord(new MediaImportItem { State = "canceled" }));
        Assert.Equal("downloading", MediaImportText.StateWord(new MediaImportItem { State = "downloading" }));

        Assert.True(MediaImportText.InFilter("active", "seeding"));
        Assert.True(MediaImportText.InFilter("done", "inLibrary"));
        Assert.True(MediaImportText.InFilter("missing", "failed"));
        Assert.False(MediaImportText.InFilter("missing", "done"));
        Assert.True(MediaImportText.InFilter("all", "canceled"));
    }
}
