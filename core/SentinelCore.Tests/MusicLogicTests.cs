using System.Text.Json;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The Music tab's rules that need no files: how credits split into artists, what a file name
// says, how search matches, LRC lyrics, the play/skip counting rule, the album and compilation
// rules, smart playlists, radio spacing and the stream/transcode decision. The scanner and the
// HTTP surface, against real audio files, are MusicScanTests and MusicEndpointsTests.
public sealed class MusicLogicTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"music-logic-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { TempDirectories.Delete(_directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // MARK: - Names

    [Theory]
    [InlineData("Fleetwood Mac", new[] { "Fleetwood Mac" })]
    [InlineData("Daft Punk feat. Pharrell Williams", new[] { "Daft Punk", "Pharrell Williams" })]
    [InlineData("Daft Punk ft. Pharrell Williams & Nile Rodgers", new[] { "Daft Punk", "Pharrell Williams", "Nile Rodgers" })]
    [InlineData("Jay-Z & Kanye West", new[] { "Jay-Z", "Kanye West" })]
    [InlineData("Queen; David Bowie", new[] { "Queen", "David Bowie" })]
    [InlineData("Simon & Garfunkel", new[] { "Simon & Garfunkel" })]
    [InlineData("Tom Petty & the Heartbreakers", new[] { "Tom Petty & the Heartbreakers" })]
    [InlineData("Earth, Wind & Fire feat. The Emotions", new[] { "Earth, Wind & Fire", "The Emotions" })]
    public void ArtistCredits_SplitIntoArtists_ButNeverInsideABand(string credit, string[] expected) =>
        Assert.Equal(expected, MusicText.SplitArtists(credit));

    [Fact]
    public void AnAmpersandBeforeThe_NamesABackingBand() =>
        Assert.Equal(["Florence & the Fakes", "Bob"], MusicText.SplitArtists("Florence & the Fakes & Bob"));

    [Fact]
    public void LibraryBands_AreNotSplit()
    {
        var bands = new HashSet<string>(["Mumford & Daughters"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["Mumford & Daughters"], MusicText.SplitArtists("Mumford & Daughters", bands));
        Assert.Equal(["Mumford", "Daughters"], MusicText.SplitArtists("Mumford & Daughters"));
    }

    [Theory]
    [InlineData("01 - Song", null, 1, "Song")]
    [InlineData("07. The Chain", null, 7, "The Chain")]
    [InlineData("1-03 Go Your Own Way", 1, 3, "Go Your Own Way")]
    [InlineData("Dreams", null, null, "Dreams")]
    [InlineData("12_Gold_Dust_Woman", null, 12, "Gold Dust Woman")]
    public void FileNames_SayTrackAndTitle(string name, int? disc, int? track, string title) =>
        Assert.Equal((disc, track, title), MusicText.ParseFileName(name));

    [Theory]
    [InlineData("flee mac", "Fleetwood Mac", true)]
    [InlineData("FLEETWOOD", "Fleetwood Mac", true)]
    [InlineData("beyonce", "Beyoncé - Halo", true)]
    [InlineData("björk", "Bjork", true)]
    [InlineData("eetwood", "Fleetwood Mac", false)]
    [InlineData("mac fleet", "Fleetwood Mac", true)]
    [InlineData("rumours dreams", "Dreams Fleetwood Mac", false)]
    public void Search_MatchesTheStartOfWords_IgnoringCaseAndAccents(string query, string text, bool matches) =>
        Assert.Equal(matches, MusicText.Matches(MusicText.Words(query), MusicText.Words(text)));

    [Fact]
    public void Ids_IgnoreCaseAccentsAndSpacing()
    {
        Assert.Equal(MusicIds.Artist("Beyoncé"), MusicIds.Artist("beyonce"));
        Assert.Equal(MusicIds.Album("Fleetwood  Mac", "Rumours"), MusicIds.Album("fleetwood mac", "RUMOURS"));
        Assert.NotEqual(MusicIds.Album("Fleetwood Mac", "Rumours"), MusicIds.Album("Fleetwood Mac", "Tusk"));
        var library = Guid.NewGuid();
        Assert.Equal(MusicIds.Track(library, "A/B/01 x.mp3"), MusicIds.Track(library, "A\\B\\01 x.mp3"));
    }

    // MARK: - Lyrics

    [Fact]
    public void Lrc_RepeatedStampsOffsetsHeadersAndWordStamps()
    {
        const string lrc = """
            [ar:Fleetwood Mac]
            [ti:Dreams]
            [offset:+500]
            [00:12.30]Now here you go again
            [00:20.00][01:05.50]<00:20.00>Thunder <00:21.00>only happens
            [00:15.5]You say you want your freedom
            """;
        var parsed = MusicLrc.Parse(lrc, "sidecar");
        Assert.True(parsed.Synced);
        Assert.Equal("sidecar", parsed.Source);
        Assert.Equal([11.8, 15.0, 19.5, 65.0], parsed.Lines.Select(l => l.Time!.Value));
        Assert.Equal("Now here you go again", parsed.Lines[0].Text);
        Assert.Equal("Thunder only happens", parsed.Lines[2].Text);
        Assert.Equal(parsed.Lines[2].Text, parsed.Lines[3].Text);
        Assert.DoesNotContain(parsed.Lines, l => l.Text.Contains("ar:"));
    }

    [Fact]
    public void Lrc_ANegativeOffsetShowsLinesLater_AndNothingGoesBelowZero()
    {
        var parsed = MusicLrc.Parse("[offset:-250]\n[00:00.10]Start\n[00:01.000]Next", "embedded");
        Assert.Equal([0.35, 1.25], parsed.Lines.Select(l => l.Time!.Value));
        Assert.Equal([0.0], MusicLrc.Parse("[offset:+1000]\n[00:00.50]Early", "x").Lines.Select(l => l.Time!.Value));
    }

    [Fact]
    public void Lrc_PlainLyrics_AreUnsyncedLines()
    {
        var parsed = MusicLrc.Parse("\nFirst line\n\nSecond verse\n", "lrclib");
        Assert.False(parsed.Synced);
        Assert.Equal(["First line", "", "Second verse"], parsed.Lines.Select(l => l.Text));
        Assert.All(parsed.Lines, l => Assert.Null(l.Time));
        Assert.Equal("First line\n\nSecond verse", parsed.Plain);
        Assert.False(MusicLrc.LooksSynced("First line"));
        Assert.True(MusicLrc.LooksSynced("x\n[01:02.03]y"));
    }

    // MARK: - Counting plays

    [Theory]
    [InlineData(180, false, 257.8, MusicPlayRules.Outcome.Play)]   // well past half
    [InlineData(129, false, 257.8, MusicPlayRules.Outcome.Play)]   // just over half
    [InlineData(120, false, 257.8, MusicPlayRules.Outcome.None)]   // under half of a long track
    [InlineData(240, true, 1200, MusicPlayRules.Outcome.Play)]     // four minutes counts whatever the length
    [InlineData(29, false, 40, MusicPlayRules.Outcome.None)]       // never under 30 s
    [InlineData(30, false, 40, MusicPlayRules.Outcome.Play)]
    [InlineData(10, true, 257.8, MusicPlayRules.Outcome.Skip)]     // skipped in the first 30 s
    [InlineData(10, false, 257.8, MusicPlayRules.Outcome.None)]    // stopped, not skipped
    [InlineData(45, true, 257.8, MusicPlayRules.Outcome.None)]     // skipped later is neither
    public void PlaysAndSkips_FollowTheCountingRule(double seconds, bool skipped, double duration, MusicPlayRules.Outcome expected) =>
        Assert.Equal(expected, MusicPlayRules.Judge(seconds, skipped, duration));

    [Fact]
    public async Task DuplicateReports_WithinFiveSeconds_AreIgnored()
    {
        using var store = new SqliteMusicStore(_directory);
        var track = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        Assert.True(await store.RecordPlayAsync(new MusicPlay("morgan", track, at, 200, false), MusicPlayRules.DuplicateWindow, Ct));
        Assert.False(await store.RecordPlayAsync(new MusicPlay("morgan", track, at.AddSeconds(4), 200, false), MusicPlayRules.DuplicateWindow, Ct));
        Assert.True(await store.RecordPlayAsync(new MusicPlay("terry", track, at.AddSeconds(1), 200, false), MusicPlayRules.DuplicateWindow, Ct));
        Assert.True(await store.RecordPlayAsync(new MusicPlay("morgan", track, at.AddSeconds(300), 200, false), MusicPlayRules.DuplicateWindow, Ct));
        Assert.Equal(2, (await store.GetPlaysAsync("morgan", Ct)).Count);
    }

    // MARK: - Albums

    private static MusicTrack Tagged(string relativePath, string title, string artist, string album, string? albumArtist = null,
        int? track = null, string[]? genres = null, int? year = null, Guid? library = null, string codec = "mp3", double duration = 200)
    {
        var libraryId = library ?? Guid.Empty;
        return new MusicTrack
        {
            Id = MusicIds.Track(libraryId, relativePath),
            LibraryId = libraryId,
            Path = "/music/" + relativePath,
            RelativePath = relativePath,
            FileSize = 1_000_000,
            AddedAt = DateTimeOffset.UtcNow.AddDays(-10),
            TagsReadAt = DateTimeOffset.UtcNow,
            Title = title,
            ArtistText = artist,
            Album = album,
            AlbumArtistTag = albumArtist,
            TrackNumber = track,
            Genres = [.. genres ?? []],
            Year = year,
            Codec = codec,
            Bitrate = codec == "flac" ? 900_000 : 128_000,
            SampleRate = 44100,
            BitDepth = codec == "flac" ? 16 : null,
            DurationSeconds = duration
        };
    }

    [Fact]
    public void ThreeArtistsOnOneAlbumWithNoAlbumArtist_IsVariousArtists()
    {
        var tracks = new List<MusicTrack>
        {
            Tagged("Now 99/01.mp3", "One", "Adele", "Now 99"),
            Tagged("Now 99/02.mp3", "Two", "Blur", "Now 99"),
            Tagged("Now 99/03.mp3", "Three", "Coldplay feat. Rihanna", "Now 99"),
            // Two artists is not a compilation: each keeps their own album.
            Tagged("Split/01.mp3", "A", "Alpha", "Split EP"),
            Tagged("Split/02.mp3", "B", "Beta", "Split EP"),
            // A featured artist does not make a third.
            Tagged("Rumours/01.mp3", "Dreams", "Fleetwood Mac", "Rumours"),
            Tagged("Rumours/02.mp3", "Duet", "Fleetwood Mac feat. Somebody", "Rumours"),
            Tagged("Rumours/03.mp3", "Other", "Fleetwood Mac & Friend", "Rumours")
        };
        MusicAlbumRules.Resolve(tracks);

        var now = tracks.Where(t => t.Album == "Now 99").ToList();
        Assert.All(now, t => Assert.True(t.Compilation));
        Assert.All(now, t => Assert.Equal(MusicText.VariousArtists, t.AlbumArtist));
        Assert.Single(now.Select(t => t.AlbumId).Distinct());
        Assert.Equal(["Coldplay", "Rihanna"], now[2].Artists);
        Assert.Equal(MusicIds.Artist("Coldplay"), now[2].ArtistId);

        var split = tracks.Where(t => t.Album == "Split EP").ToList();
        Assert.All(split, t => Assert.False(t.Compilation));
        Assert.Equal(2, split.Select(t => t.AlbumId).Distinct().Count());

        var rumours = tracks.Where(t => t.Album == "Rumours").ToList();
        Assert.Single(rumours.Select(t => t.AlbumId).Distinct());
        Assert.All(rumours, t => Assert.Equal("Fleetwood Mac", t.AlbumArtist));
    }

    [Fact]
    public void AnAlbumArtistTag_WinsAndKeepsItsBandWhole()
    {
        var tracks = new List<MusicTrack>
        {
            Tagged("SG/Bookends/01.mp3", "Mrs. Robinson", "Simon & Garfunkel", "Bookends", "Simon & Garfunkel"),
            Tagged("SG/Bookends/02.mp3", "America", "Simon & Garfunkel", "Bookends", "Simon & Garfunkel"),
            Tagged("Mix/01.mp3", "Song", "Hall & Notes", "Tagged Comp", "Various Artists"),
            Tagged("Band/01.mp3", "Tune", "Smith & Jones", "Thing", "Smith & Jones"),
            Tagged("Other/01.mp3", "Guest", "Smith & Jones", "Elsewhere"),
            Tagged("Box/CD1/01.mp3", "Disc one", "A", "Box"),
            Tagged("Box/CD2/01.mp3", "Disc two", "B", "Box"),
            Tagged("Box/CD2/02.mp3", "Disc two b", "C", "Box")
        };
        MusicAlbumRules.Resolve(tracks);
        Assert.Equal(["Simon & Garfunkel"], tracks[0].Artists);
        Assert.True(tracks[2].Compilation);
        Assert.Equal(MusicText.VariousArtists, tracks[2].AlbumArtist);
        // "Smith & Jones" is an album artist somewhere in the library, so it is a band everywhere.
        Assert.Equal(["Smith & Jones"], tracks[4].Artists);
        // Disc folders are one album folder: three artists across CD1 and CD2 make a compilation.
        Assert.All(tracks.Where(t => t.Album == "Box"), t => Assert.True(t.Compilation));
    }

    // MARK: - Smart playlists, radio, shuffle

    private async Task<MusicView> ViewAsync(IReadOnlyList<MusicTrack> tracks, Func<SqliteMusicStore, Task>? arrange = null)
    {
        MusicAlbumRules.Resolve(tracks);
        using var store = new SqliteMusicStore(_directory);
        if (arrange is not null) await arrange(store);
        var catalog = new MusicCatalog(1, tracks, new Dictionary<string, MusicArtRecord>(), []);
        return new MusicView(catalog, await MusicUserState.LoadAsync(store, "morgan", Ct), isAdmin: false);
    }

    private static MusicSmartRule Rule(string field, string op, object value) =>
        new() { Field = field, Op = op, Value = JsonSerializer.SerializeToElement(value) };

    [Fact]
    public async Task SmartRules_CoverEveryKindOfField()
    {
        var library = Guid.NewGuid();
        var old = Tagged("a/1.flac", "Old Rock", "Band A", "First", genres: ["Rock"], year: 1975, codec: "flac", library: library);
        var played = Tagged("a/2.mp3", "Played Rock", "Band A", "First", genres: ["Classic Rock", "Rock"], year: 1977);
        var jazz = Tagged("b/1.mp3", "Jazz Thing", "Miles", "Kind", genres: ["Jazz"], year: 1959, duration: 600);
        var pop = Tagged("c/1.mp3", "Beyoncé Song", "Beyoncé", "Lemonade", genres: ["Pop"], year: 2016);
        old.Composer = "John Composer";
        old.Bpm = 120;
        var tracks = new List<MusicTrack> { old, played, jazz, pop };
        var view = await ViewAsync(tracks, async store =>
        {
            await store.RecordPlayAsync(new MusicPlay("morgan", played.Id, DateTimeOffset.UtcNow.AddDays(-2), 200, false), TimeSpan.Zero, Ct);
            await store.RecordPlayAsync(new MusicPlay("morgan", jazz.Id, DateTimeOffset.UtcNow.AddDays(-200), 200, false), TimeSpan.Zero, Ct);
            await store.SetRatingAsync(new MusicRating("morgan", MusicLoveKinds.Track, old.Id, 5, DateTimeOffset.UtcNow), Ct);
            await store.SetRatingAsync(new MusicRating("morgan", MusicLoveKinds.Track, played.Id, 2, DateTimeOffset.UtcNow), Ct);
            await store.SetLoveAsync(new MusicLove("morgan", MusicLoveKinds.Track, pop.Id, true, DateTimeOffset.UtcNow), Ct);
        });
        var now = DateTimeOffset.UtcNow;
        List<string> Run(string match, params MusicSmartRule[] rules) =>
            MusicSmartPlaylists.Evaluate(view, new MusicSmartRules { Match = match, Rules = [.. rules], Sort = "title" }, now, new Random(1))
                .Select(t => t.Title).ToList();

        Assert.Equal(["Old Rock", "Played Rock"], Run("all", Rule("genre", "is", "rock")));
        Assert.Equal(["Played Rock"], Run("all", Rule("genre", "contains", "classic")));
        Assert.Equal(["Beyoncé Song", "Jazz Thing"], Run("all", Rule("genre", "notContains", "rock")));
        Assert.Equal(["Old Rock"], Run("all", Rule("genre", "is", "Rock"), Rule("rating", "gte", 4)));
        // Never played counts as "not in the last 90 days".
        Assert.Equal(["Beyoncé Song", "Jazz Thing", "Old Rock"], Run("all", Rule("lastPlayed", "notInLast", 90)));
        Assert.Equal(["Played Rock"], Run("all", Rule("lastPlayed", "inLast", 7)));
        Assert.Equal(["Beyoncé Song", "Jazz Thing", "Played Rock"], Run("all", Rule("codec", "isNot", "flac")));
        Assert.Equal(["Old Rock"], Run("all", Rule("library", "is", library.ToString())));
        Assert.Equal(["Jazz Thing", "Old Rock", "Played Rock"], Run("all", Rule("year", "lte", 1980)));
        Assert.Equal(["Jazz Thing"], Run("all", Rule("durationSeconds", "gte", 300)));
        Assert.Equal(["Beyoncé Song"], Run("all", Rule("loved", "is", true)));
        Assert.Equal(["Beyoncé Song"], Run("all", Rule("artist", "is", "beyonce")));
        Assert.Equal(["Old Rock"], Run("all", Rule("composer", "contains", "composer")));
        Assert.Equal(["Old Rock"], Run("all", Rule("bpm", "is", 120)));
        Assert.Equal(["Jazz Thing", "Played Rock"], Run("all", Rule("playCount", "gte", 1)));
        Assert.Equal(["Jazz Thing", "Old Rock"], Run("any", Rule("genre", "is", "jazz"), Rule("rating", "is", 5)));
        Assert.Equal(["Beyoncé Song", "Jazz Thing", "Old Rock", "Played Rock"], Run("all", Rule("added", "inLast", 30)));
        Assert.Empty(Run("all", Rule("added", "notInLast", 30)));

        var byRating = MusicSmartPlaylists.Evaluate(view, new MusicSmartRules { Sort = "rating", Order = "desc", Limit = 2 }, now, new Random(1));
        Assert.Equal(["Old Rock", "Played Rock"], byRating.Select(t => t.Title));
    }

    [Fact]
    public void SmartRules_ThatCannotWork_AreRefusedInPlainEnglish()
    {
        var errors = MusicSmartPlaylists.Validate(new MusicSmartRules
        {
            Match = "most",
            Sort = "colour",
            Rules = [Rule("genre", "gte", 3), Rule("mood", "is", "happy"), Rule("year", "is", "nineteen"), Rule("loved", "is", "maybe")]
        });
        Assert.Contains("Match must be all or any.", errors);
        Assert.Contains(errors, e => e.StartsWith("Sort must be one of"));
        Assert.Contains(errors, e => e.StartsWith("Rule 1: genre can be matched with is, isNot, contains, notContains"));
        Assert.Contains(errors, e => e.StartsWith("Rule 2: \"mood\""));
        Assert.Contains(errors, e => e.StartsWith("Rule 3: year needs a number"));
        Assert.Contains(errors, e => e.StartsWith("Rule 4: loved needs a true or false"));
        Assert.Empty(MusicSmartPlaylists.Validate(new MusicSmartRules { Rules = [Rule("lastPlayed", "notInLast", 90)] }));
    }

    [Fact]
    public async Task Radio_NeverRepeatsAnArtistWithinThreeTracks_AndLeadsWithTheSeed()
    {
        var tracks = new List<MusicTrack>();
        foreach (var artist in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
            for (var i = 0; i < 12; i++)
                tracks.Add(Tagged($"{artist}/{i:00}.mp3", $"{artist} {i}", artist, $"{artist} Album", genres: ["Rock"], year: 1990 + i));
        var heard = tracks[5];
        var view = await ViewAsync(tracks, store =>
            store.RecordPlayAsync(new MusicPlay("morgan", heard.Id, DateTimeOffset.UtcNow.AddMinutes(-10), 200, false), TimeSpan.Zero, Ct));
        var seed = tracks[0];
        var result = MusicService.Radio(view, "track", seed.Id.ToString(), [], new HashSet<Guid> { tracks[1].Id }, 40, DateTimeOffset.UtcNow);

        Assert.NotNull(result);
        Assert.Equal("Alpha 0 Radio", result.Name);
        Assert.Equal(seed.Id, result.Tracks[0].Id);
        Assert.Equal(result.Tracks.Count, result.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.DoesNotContain(result.Tracks, t => t.Id == tracks[1].Id);   // excluded by the client
        Assert.DoesNotContain(result.Tracks, t => t.Id == heard.Id);        // played in the last hour
        for (var i = 0; i < result.Tracks.Count; i++)
            for (var j = i + 1; j < Math.Min(result.Tracks.Count, i + 4); j++)
                Assert.NotEqual(result.Tracks[i].ArtistId, result.Tracks[j].ArtistId);
        Assert.True(result.Tracks.Count >= 35, $"only {result.Tracks.Count} tracks");

        // Asking again with everything so far excluded carries the station on.
        var more = MusicService.Radio(view, "track", seed.Id.ToString(), [], result.Tracks.Select(t => t.Id).ToHashSet(), 40, DateTimeOffset.UtcNow);
        Assert.NotNull(more);
        Assert.Empty(more.Tracks.Select(t => t.Id).Intersect(result.Tracks.Select(t => t.Id)));
    }

    [Fact]
    public async Task Mixes_ClusterTheGenresSomeonePlays_AndShuffleAllPlaysEverything()
    {
        var tracks = new List<MusicTrack>();
        for (var i = 0; i < 6; i++) tracks.Add(Tagged($"r/{i}.mp3", $"Rock {i}", $"Rocker {i % 3}", "Rock Record", genres: i == 5 ? ["Rock"] : ["Rock", "Classic Rock"], year: 1975));
        for (var i = 0; i < 6; i++) tracks.Add(Tagged($"j/{i}.mp3", $"Jazz {i}", $"Jazzer {i % 3}", "Jazz Record", genres: ["Jazz"], year: 1962));
        for (var i = 0; i < 2; i++) tracks.Add(Tagged($"f/{i}.mp3", $"Folk {i}", "Folkie", "Unheard", genres: ["Folk"], year: 2001));
        var view = await ViewAsync(tracks, async store =>
        {
            foreach (var track in tracks.Take(4).Append(tracks[5]))
                await store.RecordPlayAsync(new MusicPlay("morgan", track.Id, DateTimeOffset.UtcNow.AddDays(-3), 200, false), TimeSpan.Zero, Ct);
            await store.RecordPlayAsync(new MusicPlay("morgan", tracks[8].Id, DateTimeOffset.UtcNow.AddDays(-3), 200, false), TimeSpan.Zero, Ct);
        });
        var mixes = MusicDiscovery.Mixes(view, DateTimeOffset.UtcNow);
        var daily1 = mixes.Single(m => m.Dto.Id == "daily-1").Dto;
        Assert.Equal("Rock and Classic Rock", daily1.Description);
        Assert.Equal("genre", daily1.SeedType);
        Assert.Equal("rock", daily1.SeedId);
        Assert.Equal("Jazz", mixes.Single(m => m.Dto.Id == "daily-2").Dto.Description);
        Assert.Contains(mixes, m => m.Dto.Id == "decade-1970" && m.Dto.Name == "70s Mix");
        Assert.Contains(mixes, m => m.Dto.Id == "discover");

        var mix = MusicService.Mix(view, "daily-1", new HashSet<Guid>(), 50, DateTimeOffset.UtcNow);
        Assert.NotNull(mix);
        Assert.All(mix.Tracks, t => Assert.Contains("Rock", t.Genres));

        var shuffled = MusicDiscovery.Shuffle(view, 200, DateTimeOffset.UtcNow, new Random(3));
        Assert.Equal(tracks.Count, shuffled.Select(t => t.Id).Distinct().Count());
    }

    // MARK: - Streaming decisions

    [Fact]
    public void Streaming_ServesTheOriginalWhenTheDeviceCan_AndConvertsOtherwise()
    {
        var flac = Tagged("a.flac", "a", "a", "a", codec: "flac");
        var mp3 = Tagged("b.mp3", "b", "b", "b");
        var apple = MusicTranscoder.ParseCodecs("mp3,aac,alac,flac,wav,aiff");
        var web = MusicTranscoder.ParseCodecs(null);

        Assert.Equal((false, 0), MusicTranscoder.Decide(flac, apple, null, 256));
        Assert.Equal((true, 256), MusicTranscoder.Decide(flac, web, null, 256));
        // Data saver: a FLAC is far over 160 kbps, so even a device that plays FLAC gets AAC.
        Assert.Equal((true, 160), MusicTranscoder.Decide(flac, apple, 160, 256));
        Assert.Equal((false, 0), MusicTranscoder.Decide(mp3, web, 160, 256));
        // A 128 kbps MP3 for a device without MP3 is not inflated to 256.
        Assert.Equal((true, 128), MusicTranscoder.Decide(mp3, MusicTranscoder.ParseCodecs("aac"), null, 256));
        Assert.Contains("vorbis", MusicTranscoder.ParseCodecs("ogg,opus"));
        Assert.Equal("audio/flac", MusicTranscoder.ContentType("x.FLAC"));
    }
}
