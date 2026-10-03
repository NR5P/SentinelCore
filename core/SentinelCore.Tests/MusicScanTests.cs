using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The music scanner against real files made by ffmpeg: tags from every common format, the
// name-and-folder fallbacks, compilations, which cover wins, sidecar and embedded lyrics, a
// library on another computer's share (TestSmbServer), and the AAC conversion cache.
public sealed class MusicScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"music-scan-{Guid.NewGuid():N}");
    private readonly string _library;
    private readonly string _data;
    private readonly string _settingsPath;

    public MusicScanTests()
    {
        _library = Path.Combine(_root, "Music");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_data);
        _settingsPath = Path.Combine(_root, "appsettings.json");
        // Never reach the internet from a test.
        File.WriteAllText(_settingsPath, """{ "Music": { "FetchArtworkOnline": false, "FetchLyricsOnline": false } }""");
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class PlainHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed record Rig(SqliteMusicStore Store, MusicScanner Scanner, MediaFiles Files, MediaFileRelay Relay,
        MediaNetworkComputerStore Computers, MusicArtCache Art) : IDisposable
    {
        public void Dispose()
        {
            Scanner.Dispose();
            Relay.Dispose();
            Files.Network.Dispose();
            Store.Dispose();
        }
    }

    private Rig NewRig()
    {
        var store = new SqliteMusicStore(_data);
        var computers = new MediaNetworkComputerStore(_data);
        var network = new MediaNetworkFileSystem(computers, new MediaNetworkResolver(NullLogger<MediaNetworkResolver>.Instance), NullLogger<MediaNetworkFileSystem>.Instance);
        var relay = new MediaFileRelay(network, NullLogger<MediaFileRelay>.Instance);
        var files = new MediaFiles(network, relay);
        var art = new MusicArtCache(Path.Combine(_data, "music-art"));
        var scanner = new MusicScanner(store, files, art, new MusicCoverArtClient(new PlainHttp(), NullLogger<MusicCoverArtClient>.Instance),
            new AppSettingsFile(_settingsPath), new ConfigurationBuilder().Build(), NullLogger<MusicScanner>.Instance);
        return new Rig(store, scanner, files, relay, computers, art);
    }

    private static async Task<MusicCatalog> ScanAsync(Rig rig, MusicLibrary library)
    {
        await rig.Store.SaveLibraryAsync(library, Ct);
        Assert.True(await rig.Scanner.ScanAsync(null, Ct));
        Assert.Null(rig.Scanner.LastError);
        return await new MusicCatalogCache(rig.Store).GetAsync(Ct);
    }

    [Fact]
    public async Task EveryCommonFormat_ScansWithItsTags()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var album = Path.Combine(_library, "Fleetwood Mac", "Rumours");
        var flacTags = MusicTestAudio.Tags("Dreams", "Fleetwood Mac", "Rumours", 2, "Fleetwood Mac", "Rock", 1977);
        flacTags["REPLAYGAIN_TRACK_GAIN"] = "-6.20 dB";
        flacTags["REPLAYGAIN_TRACK_PEAK"] = "0.98";
        flacTags["LYRICS"] = "[00:01.00]Now here you go again";
        await MusicTestAudio.MakeAsync(Path.Combine(album, "02 Dreams.flac"), flacTags);
        await MusicTestAudio.MakeAsync(Path.Combine(album, "01 Second Hand News.mp3"),
            MusicTestAudio.Tags("Second Hand News", "Fleetwood Mac", "Rumours", 1, "Fleetwood Mac", "Rock", 1977));
        await MusicTestAudio.MakeAsync(Path.Combine(album, "03 Never Going Back.m4a"),
            MusicTestAudio.Tags("Never Going Back Again", "Fleetwood Mac", "Rumours", 3, "Fleetwood Mac", "Rock", 1977));
        await MusicTestAudio.MakeAsync(Path.Combine(album, "04 Dont Stop.ogg"),
            MusicTestAudio.Tags("Don't Stop", "Fleetwood Mac", "Rumours", 4, "Fleetwood Mac", "Rock", 1977));
        await MusicTestAudio.MakeAsync(Path.Combine(album, "05 Go Your Own Way.opus"),
            MusicTestAudio.Tags("Go Your Own Way", "Fleetwood Mac", "Rumours", 5, "Fleetwood Mac", "Rock", 1977));
        await File.WriteAllTextAsync(Path.Combine(album, "01 Second Hand News.lrc"), "[00:00.50]I know there's nothing to say", Ct);
        // No tags at all: everything comes from the names.
        await MusicTestAudio.MakeAsync(Path.Combine(_library, "Some Band", "Tapes", "07 - Basement Song.wav"), seconds: 3);
        // Skipped: too small, hidden, the recycle bin and a macOS resource fork.
        await File.WriteAllBytesAsync(Path.Combine(album, "tiny.mp3"), new byte[1000], Ct);
        await MusicTestAudio.MakeAsync(Path.Combine(_library, ".hidden", "secret.mp3"));
        await MusicTestAudio.MakeAsync(Path.Combine(_library, "$RECYCLE.BIN", "deleted.mp3"));
        File.Copy(Path.Combine(album, "01 Second Hand News.mp3"), Path.Combine(album, "._01 Second Hand News.mp3"));

        using var rig = NewRig();
        var library = new MusicLibrary(Guid.NewGuid(), "Music", _library, DateTimeOffset.UtcNow);
        var catalog = await ScanAsync(rig, library);

        Assert.Equal(6, catalog.Tracks.Count);
        var byTitle = catalog.Tracks.ToDictionary(t => t.Title);
        Assert.Equal(("flac", 2, 1977), (byTitle["Dreams"].Codec, byTitle["Dreams"].TrackNumber, byTitle["Dreams"].Year));
        Assert.Equal(16, byTitle["Dreams"].BitDepth);
        Assert.Equal(-6.2, byTitle["Dreams"].TrackGain);
        Assert.Equal(0.98, byTitle["Dreams"].TrackPeak);
        Assert.True(byTitle["Dreams"].HasEmbeddedLyrics);
        Assert.Equal("mp3", byTitle["Second Hand News"].Codec);
        Assert.NotNull(byTitle["Second Hand News"].LyricsSidecar);
        Assert.Equal("aac", byTitle["Never Going Back Again"].Codec);
        Assert.Equal("vorbis", byTitle["Don't Stop"].Codec);
        Assert.Equal("opus", byTitle["Go Your Own Way"].Codec);
        Assert.All(byTitle.Values.Where(t => t.Album == "Rumours"), t =>
        {
            Assert.Equal(["Rock"], t.Genres);
            Assert.InRange(t.DurationSeconds, 7.5, 8.6);
            Assert.Equal(MusicIds.Album("Fleetwood Mac", "Rumours"), t.AlbumId);
        });
        Assert.Single(catalog.Albums, a => a.Title == "Rumours");
        Assert.Equal([1, 2, 3, 4, 5], catalog.AlbumsById[MusicIds.Album("Fleetwood Mac", "Rumours")].Tracks.Select(t => t.TrackNumber!.Value));

        var basement = byTitle["Basement Song"];
        Assert.Equal((7, "Tapes", "Some Band", "wav"), (basement.TrackNumber!.Value, basement.Album, basement.ArtistText, basement.Codec));
        Assert.Equal(MusicIds.Track(library.Id, "Some Band/Tapes/07 - Basement Song.wav"), basement.Id);

        // A rescan finds the same ids and reads nothing again.
        var before = catalog.Tracks.Select(t => (t.Id, t.TagsReadAt)).OrderBy(x => x.Id).ToList();
        Assert.True(await rig.Scanner.ScanAsync(null, Ct));
        var again = await rig.Store.GetTracksAsync(null, false, Ct);
        Assert.Equal(before, again.Select(t => (t.Id, t.TagsReadAt)).OrderBy(x => x.Id).ToList());

        // A deleted file goes missing; the rest stay.
        File.Delete(Path.Combine(album, "03 Never Going Back.m4a"));
        Assert.True(await rig.Scanner.ScanAsync(null, Ct));
        Assert.Equal(5, (await rig.Store.GetTracksAsync(null, false, Ct)).Count);
    }

    [Fact]
    public async Task Compilations_AndWhichCoverWins()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var embeddedCover = Path.Combine(_root, "blue.png");
        await MusicTestAudio.MakeImageAsync(embeddedCover, "blue");

        // Three artists, no album artist: Various Artists. It has an embedded cover AND a
        // cover.jpg; the embedded picture of the first track wins.
        var comp = Path.Combine(_library, "Summer Hits");
        await MusicTestAudio.MakeAsync(Path.Combine(comp, "01.mp3"), MusicTestAudio.Tags("Sun", "Artist One", "Summer Hits", 1), coverImage: embeddedCover);
        await MusicTestAudio.MakeAsync(Path.Combine(comp, "02.mp3"), MusicTestAudio.Tags("Sea", "Artist Two", "Summer Hits", 2));
        await MusicTestAudio.MakeAsync(Path.Combine(comp, "03.flac"), MusicTestAudio.Tags("Sand", "Artist Three feat. Artist One", "Summer Hits", 3));
        await MusicTestAudio.MakeImageAsync(Path.Combine(comp, "cover.jpg"), "green");

        // Only a folder.jpg (in the album folder above its disc folder).
        var boxed = Path.Combine(_library, "Band", "Boxed Set");
        await MusicTestAudio.MakeAsync(Path.Combine(boxed, "CD1", "01.mp3"), MusicTestAudio.Tags("One", "Band", "Boxed Set", 1));
        await MusicTestAudio.MakeAsync(Path.Combine(boxed, "CD2", "01.mp3"), MusicTestAudio.Tags("Two", "Band", "Boxed Set", 1));
        await MusicTestAudio.MakeImageAsync(Path.Combine(boxed, "Folder.JPG"), "yellow");
        await MusicTestAudio.MakeImageAsync(Path.Combine(_library, "Band", "artist.jpg"), "purple");

        // Nothing at all, and fetching online is off.
        await MusicTestAudio.MakeAsync(Path.Combine(_library, "Bare", "Bare Album", "01.mp3"), MusicTestAudio.Tags("Bare", "Bare", "Bare Album", 1));

        using var rig = NewRig();
        var catalog = await ScanAsync(rig, new MusicLibrary(Guid.NewGuid(), "Music", _library, DateTimeOffset.UtcNow));

        var summer = Assert.Single(catalog.Albums, a => a.Title == "Summer Hits");
        Assert.True(summer.Compilation);
        Assert.Equal(MusicText.VariousArtists, summer.Artist);
        Assert.Equal(3, summer.Tracks.Count);
        Assert.StartsWith(rig.Art.Root, summer.ArtPath);
        Assert.Equal("embedded", (await rig.Store.GetArtAsync(Ct))[MusicArtCache.AlbumKey(summer.Id)].Source);
        // Artist One is on the compilation twice (once featured) but owns no album of their own.
        var one = catalog.ArtistsById[MusicIds.Artist("Artist One")];
        Assert.Empty(one.Albums);
        Assert.Equal([summer.Id], one.AppearsOn.Select(a => a.Id));
        Assert.Equal(2, one.Tracks.Count);

        var box = Assert.Single(catalog.Albums, a => a.Title == "Boxed Set");
        Assert.EndsWith("Folder.JPG", box.ArtPath);
        Assert.Equal(2, box.DiscCount);
        Assert.Equal([1, 2], box.Tracks.Select(t => t.DiscNumber!.Value));
        Assert.EndsWith("artist.jpg", catalog.ArtistsById[MusicIds.Artist("Band")].ImagePath);

        var bare = Assert.Single(catalog.Albums, a => a.Title == "Bare Album");
        Assert.Null(bare.ArtPath);
        // An artist with no picture of their own shows their newest album's cover - here none.
        Assert.Null(catalog.ArtistArt(catalog.ArtistsById[MusicIds.Artist("Bare")]));
        Assert.EndsWith("artist.jpg", catalog.ArtistArt(catalog.ArtistsById[MusicIds.Artist("Band")]));

        // Sized copies are made once, and never bigger than asked.
        var sized = await rig.Art.SizedAsync(box.ArtPath!, LocalMediaFileSystem.Instance, 100, Ct);
        Assert.NotEqual(box.ArtPath, sized);
        using (var image = await SixLabors.ImageSharp.Image.LoadAsync(sized, Ct)) Assert.Equal(128, image.Width);
        Assert.Equal(sized, await rig.Art.SizedAsync(box.ArtPath!, LocalMediaFileSystem.Instance, 128, Ct));
        // Asking for more than the original has gives the original.
        Assert.Equal(box.ArtPath, await rig.Art.SizedAsync(box.ArtPath!, LocalMediaFileSystem.Instance, 1200, Ct));
    }

    [Fact]
    public async Task ALibraryOnAnotherComputersShare_ScansThroughSmb()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var shared = Path.Combine(_root, "shared");
        var cover = Path.Combine(_root, "red.png");
        await MusicTestAudio.MakeImageAsync(cover);
        await MusicTestAudio.MakeAsync(Path.Combine(shared, "Music", "Queen", "Jazz", "01 Mustapha.flac"),
            MusicTestAudio.Tags("Mustapha", "Queen", "Jazz", 1, "Queen", "Rock", 1978), coverImage: cover);
        await File.WriteAllTextAsync(Path.Combine(shared, "Music", "Queen", "Jazz", "01 Mustapha.lrc"), "[00:01.00]Ibrahim", Ct);

        using var server = new TestSmbServer(new Dictionary<string, string> { ["Media"] = shared });
        using var rig = NewRig();
        rig.Computers.Save(new MediaNetworkComputer(Guid.NewGuid(), server.Host, TestSmbServer.Username, TestSmbServer.Password, "", DateTimeOffset.UtcNow));
        var library = new MusicLibrary(Guid.NewGuid(), "Den PC", $"smb://{server.Host}/Media/Music", DateTimeOffset.UtcNow);
        var catalog = await ScanAsync(rig, library);

        var track = Assert.Single(catalog.Tracks);
        Assert.Equal(("Mustapha", "Queen", "Jazz", "flac", 1978), (track.Title, track.ArtistText, track.Album, track.Codec, track.Year));
        Assert.Equal($"smb://{server.Host}/Media/Music/Queen/Jazz/01 Mustapha.flac", track.Path);
        Assert.Equal(MusicIds.Track(library.Id, "Queen/Jazz/01 Mustapha.flac"), track.Id);
        Assert.True(track.HasEmbeddedArt);
        Assert.NotNull(catalog.AlbumOf(track)!.ArtPath);

        // Lyrics are read through the share too.
        var lyrics = new MusicLyricsService(rig.Store, rig.Files, new PlainHttp(), new AppSettingsFile(_settingsPath), NullLogger<MusicLyricsService>.Instance);
        var found = await lyrics.GetAsync(track, Ct);
        Assert.NotNull(found);
        Assert.Equal(("sidecar", "Ibrahim"), (found.Source, found.Lines[0].Text));
    }

    [Fact]
    public async Task Lyrics_PreferTheSidecar_ThenEmbedded_AndNothingOnlineWhenOff()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        var folder = Path.Combine(_library, "A", "B");
        var both = MusicTestAudio.Tags("Both", "A", "B", 1);
        both["LYRICS"] = "Embedded words";
        await MusicTestAudio.MakeAsync(Path.Combine(folder, "01 Both.flac"), both);
        await File.WriteAllTextAsync(Path.Combine(folder, "01 Both.lrc"), "﻿[00:02.00]Sidecar words", Ct);
        var embedded = MusicTestAudio.Tags("Embedded", "A", "B", 2);
        embedded["LYRICS"] = "[00:03.00]Synced inside\n[00:05.00]the file";
        await MusicTestAudio.MakeAsync(Path.Combine(folder, "02 Embedded.flac"), embedded);
        await MusicTestAudio.MakeAsync(Path.Combine(folder, "03 None.flac"), MusicTestAudio.Tags("None", "A", "B", 3));

        using var rig = NewRig();
        var catalog = await ScanAsync(rig, new MusicLibrary(Guid.NewGuid(), "Music", _library, DateTimeOffset.UtcNow));
        var lyrics = new MusicLyricsService(rig.Store, rig.Files, new PlainHttp(), new AppSettingsFile(_settingsPath), NullLogger<MusicLyricsService>.Instance);
        var byTitle = catalog.Tracks.ToDictionary(t => t.Title);

        var sidecar = await lyrics.GetAsync(byTitle["Both"], Ct);
        Assert.Equal(("sidecar", true, "Sidecar words"), (sidecar!.Source, sidecar.Synced, sidecar.Lines[0].Text));
        var inside = await lyrics.GetAsync(byTitle["Embedded"], Ct);
        Assert.Equal(("embedded", true), (inside!.Source, inside.Synced));
        Assert.Equal([3.0, 5.0], inside.Lines.Select(l => l.Time!.Value));
        Assert.False(byTitle["None"].HasLyrics);
        Assert.Null(await lyrics.GetAsync(byTitle["None"], Ct));
    }

    [Fact]
    public async Task Conversions_AreAacInM4a_SharedBetweenAsks_AndTheCacheIsTrimmed()
    {
        if (!await MusicTestAudio.FfmpegAvailableAsync()) Assert.Skip("needs a real ffmpeg on PATH");
        await MusicTestAudio.MakeAsync(Path.Combine(_library, "X", "Y", "01 Song.flac"), MusicTestAudio.Tags("Song", "X", "Y", 1));
        using var rig = NewRig();
        var catalog = await ScanAsync(rig, new MusicLibrary(Guid.NewGuid(), "Music", _library, DateTimeOffset.UtcNow));
        var track = Assert.Single(catalog.Tracks);
        var cache = Path.Combine(_data, "audio-cache");
        var transcoder = new MusicTranscoder(rig.Files, cache, "ffmpeg", NullLogger<MusicTranscoder>.Instance);

        var asks = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => transcoder.ConvertAsync(track, 128, Ct)));
        Assert.Single(asks.Distinct());
        Assert.Single(Directory.GetFiles(cache));
        Assert.EndsWith(".m4a", asks[0]);

        // What came out is AAC, starts with its index (faststart) and is as long as the original.
        var probe = new MusicTrack { Path = asks[0], RelativePath = "converted.m4a" };
        await MusicTags.ReadAsync(probe, rig.Files, "ffprobe", NullLogger.Instance, Ct);
        Assert.Equal("aac", probe.Codec);
        Assert.InRange(probe.DurationSeconds, 7.5, 8.6);
        Assert.True(await MoovBeforeMdatAsync(asks[0]), "the index (moov) should come before the audio (mdat)");

        // Trimming keeps the cache under its limit, oldest-served first.
        var small = new MusicTranscoder(rig.Files, cache, "ffmpeg", NullLogger<MusicTranscoder>.Instance) { CacheLimit = new FileInfo(asks[0]).Length + 10 };
        var old = Path.Combine(cache, "old.m4a");
        await File.WriteAllBytesAsync(old, new byte[100], Ct);
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(asks[0], DateTime.UtcNow);
        small.Trim();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(asks[0]));
    }

    private static async Task<bool> MoovBeforeMdatAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var moov = text.IndexOf("moov", StringComparison.Ordinal);
        var mdat = text.IndexOf("mdat", StringComparison.Ordinal);
        return moov >= 0 && mdat >= 0 && moov < mdat;
    }
}
