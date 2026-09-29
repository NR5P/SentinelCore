using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The pure parts of movie downloads: reading release names, deciding what matches, ordering the
// list, planning where finished files go, and reading the VPN watcher's status file.
public sealed class MediaDownloadLogicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"media-download-logic-{Guid.NewGuid():N}");

    public MediaDownloadLogicTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static MediaReleaseRanker.Request Movie(string title, int? year) => new(MediaKinds.Movie, title, year, null, null, "1080p");

    private static MediaReleaseRanker.Request Episode(string title, int? season, int? episode) => new(MediaKinds.Episode, title, null, season, episode, "1080p");

    [Theory]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.Atmos.DV.HDR.H.265-FLUX", "2160p", "HEVC", "WEB-DL", true)]
    [InlineData("Big Buck Bunny (2008) [1080p] [BluRay] [YTS.MX]", "1080p", null, "BluRay", false)]
    [InlineData("The.Matrix.1999.720p.BRRip.x264-YIFY", "720p", "H.264", "BluRay", false)]
    [InlineData("Some.Movie.2023.HDCAM.x264", null, "H.264", "CAM", false)]
    [InlineData("Show.S01E02.1080p.AMZN.WEBRip.AV1", "1080p", "AV1", "WEBRip", false)]
    public void Parse_ReadsQualityCodecSourceAndHdr(string name, string? quality, string? codec, string? source, bool hdr)
    {
        var parsed = MediaReleaseRanker.Parse(name);
        Assert.Equal(quality, parsed.Quality);
        Assert.Equal(codec, parsed.Codec);
        Assert.Equal(source, parsed.Source);
        Assert.Equal(hdr, parsed.Hdr);
    }

    [Fact]
    public void Parse_ReadsEpisodesSeasonPacksAndMultiSeasonPacks()
    {
        var episode = MediaReleaseRanker.Parse("Bluey.2018.S02E05.1080p.WEB-DL.AAC2.0.H.264");
        Assert.Equal((2, 5, false), (episode.Season, episode.Episode, episode.SeasonPack));
        Assert.Equal("Bluey", episode.Title);

        var pack = MediaReleaseRanker.Parse("Bluey.S02.1080p.WEB-DL.AAC2.0.H.264-NTb");
        Assert.Equal((2, (int?)null, true, false), (pack.Season, pack.Episode, pack.SeasonPack, pack.MultiSeason));

        var worded = MediaReleaseRanker.Parse("The Office (US) Season 3 Complete 720p");
        Assert.Equal((3, true), (worded.Season, worded.SeasonPack));

        var multi = MediaReleaseRanker.Parse("Bluey.S01-S03.1080p.WEB-DL");
        Assert.True(multi.MultiSeason);
        Assert.False(multi.SeasonPack);
    }

    [Fact]
    public void Matches_MoviesNeedTheTitleAndAYearWithinOne()
    {
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Dune.Part.Two.2024.1080p.WEB-DL"), Movie("Dune: Part Two", 2024)));
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Dune.Part.Two.2023.1080p.WEB-DL"), Movie("Dune: Part Two", 2024)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Dune.1984.1080p.BluRay"), Movie("Dune", 2021)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Dune.Part.Two.2024.1080p"), Movie("Dune", 2021)));
        // An episode of a show that happens to share the name is not the film.
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Fargo.S01E01.720p"), Movie("Fargo", 1996)));
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Bobs.Burgers.The.Movie.2022.1080p"), Movie("The Bob's Burgers Movie", 2022)) is false);
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Bobs.Burgers.Movie.2022.1080p"), Movie("The Bob's Burgers Movie", 2022)));
    }

    [Fact]
    public void Matches_TvNeedsTheEpisodeOrExactlyThatSeasonsPack()
    {
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S02E03.720p.WEB"), Episode("The Office", 2, 3)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S02E04.720p.WEB"), Episode("The Office", 2, 3)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S02.720p.WEB"), Episode("The Office", 2, 3)));
        Assert.True(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S02.720p.WEB"), Episode("The Office", 2, null)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S03.720p.WEB"), Episode("The Office", 2, null)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("The.Office.US.S01-S09.720p"), Episode("The Office", 2, null)));
        Assert.False(MediaReleaseRanker.Matches(MediaReleaseRanker.Parse("Parks.and.Recreation.S02E03"), Episode("The Office", 2, 3)));
    }

    [Fact]
    public void Score_PutsWhatWasAskedForAndWellSharedFirst_AndCamsLast()
    {
        var request = Movie("Big Buck Bunny", 2008);
        double Score(string name, int seeders, long size = 2_000_000_000)
        {
            var parsed = MediaReleaseRanker.Parse(name);
            return MediaReleaseRanker.Score(parsed, MediaReleaseRanker.Matches(parsed, request), seeders, size, "1080p");
        }

        var good = Score("Big.Buck.Bunny.2008.1080p.BluRay.x264", 40);
        var fourK = Score("Big.Buck.Bunny.2008.2160p.BluRay.x265", 40, 12_000_000_000);
        var small = Score("Big.Buck.Bunny.2008.720p.BluRay.x264", 40);
        var cam = Score("Big.Buck.Bunny.2008.CAM.x264", 400);
        var dead = Score("Big.Buck.Bunny.2008.1080p.BluRay.x264", 0);
        var other = Score("Elephants.Dream.2006.1080p.BluRay.x264", 900);

        Assert.True(good > fourK && fourK > small, $"{good} {fourK} {small}");
        Assert.True(small > cam && small > dead, $"{small} {cam} {dead}");
        Assert.True(cam > other, "anything that matches beats anything that does not");
        Assert.Equal("Filmed in a cinema - poor picture and sound.", MediaReleaseRanker.Warning(MediaReleaseRanker.Parse("X.2008.CAM"), 5));
        Assert.NotNull(MediaReleaseRanker.Warning(MediaReleaseRanker.Parse("X.2008.1080p"), 0));
    }

    private MediaLibrary Library(string kind) =>
        new(Guid.NewGuid(), kind == MediaLibraryKinds.Movies ? "Movies" : "TV", kind, Path.Combine(_root, kind), DateTimeOffset.UtcNow);

    [Fact]
    public void Plan_Movie_TakesTheBiggestVideo_AndItsEnglishSubtitle_AndNamesItForTheScanner()
    {
        var library = Library(MediaLibraryKinds.Movies);
        var download = new MediaDownload { Kind = MediaKinds.Movie, Title = "Dune: Part Two", Year = 2024 };
        var staging = Path.Combine(_root, "staging");
        var plan = MediaDownloadImporter.Plan(download,
        [
            new("Dune.Part.Two.2024.1080p/Dune.Part.Two.2024.1080p.mkv", 5_000),
            new("Dune.Part.Two.2024.1080p/Sample/dune.sample.mkv", 900_000),
            new("Dune.Part.Two.2024.1080p/Subs/2_English.srt", 30),
            new("Dune.Part.Two.2024.1080p/Subs/3_French.srt", 30),
            new("Dune.Part.Two.2024.1080p/RARBG.txt", 1),
        ], staging, library, null);

        var folder = Path.Combine(library.Path, "Dune Part Two (2024)");
        Assert.Equal(
        [
            new(Path.Combine(staging, "Dune.Part.Two.2024.1080p/Dune.Part.Two.2024.1080p.mkv"), Path.Combine(folder, "Dune Part Two (2024).mkv")),
            new(Path.Combine(staging, "Dune.Part.Two.2024.1080p/Subs/2_English.srt"), Path.Combine(folder, "Dune Part Two (2024).en.srt")),
        ], plan);
        // What the scanner will make of that name.
        Assert.Equal(("Dune Part Two", (int?)2024), (MediaNaming.ParseTitle("Dune Part Two (2024)").Title, MediaNaming.ParseTitle("Dune Part Two (2024)").Year));
    }

    [Fact]
    public void Plan_SeasonPack_FilesEveryEpisodeOfThatSeason_IntoAnExistingShowFolder()
    {
        var library = Library(MediaLibraryKinds.Shows);
        var existing = Path.Combine(library.Path, "Bluey (2018) Season 1");
        var download = new MediaDownload { Kind = MediaKinds.Episode, Title = "Bluey", Year = 2018, Season = 2 };
        var plan = MediaDownloadImporter.Plan(download,
        [
            new("Bluey.S02.1080p/Bluey.S02E01.Dance.Mode.1080p.mkv", 300),
            new("Bluey.S02.1080p/Bluey.S02E02.Hammerbarn.1080p.mkv", 300),
            new("Bluey.S02.1080p/Bluey.S02E02.Hammerbarn.1080p.PROPER.mkv", 310),
            new("Bluey.S02.1080p/Bluey.S03E01.Perfect.mkv", 300),
            new("Bluey.S02.1080p/Bluey.S02E01.Dance.Mode.1080p.en.srt", 3),
        ], "/staging", library, existing);

        Assert.Equal(
        [
            ("/staging/Bluey.S02.1080p/Bluey.S02E02.Hammerbarn.1080p.PROPER.mkv", Path.Combine(existing, "Season 2", "Bluey - S02E02.mkv")),
            ("/staging/Bluey.S02.1080p/Bluey.S02E01.Dance.Mode.1080p.mkv", Path.Combine(existing, "Season 2", "Bluey - S02E01.mkv")),
            ("/staging/Bluey.S02.1080p/Bluey.S02E01.Dance.Mode.1080p.en.srt", Path.Combine(existing, "Season 2", "Bluey - S02E01.en.srt")),
        ], plan.Select(m => (m.Source, m.Destination)));
    }

    [Fact]
    public void Plan_OneEpisode_TakesOnlyThatEpisode_AndStartsANewShowFolder()
    {
        var library = Library(MediaLibraryKinds.Shows);
        var download = new MediaDownload { Kind = MediaKinds.Episode, Title = "The Office", Year = 2005, Season = 2, Episode = 3 };
        var plan = MediaDownloadImporter.Plan(download, [new("The.Office.US.S02E03.720p.mkv", 100)], "/s", library, null);
        Assert.Equal(Path.Combine(library.Path, "The Office (2005)", "Season 2", "The Office - S02E03.mkv"), Assert.Single(plan).Destination);
    }

    [Fact]
    public void Execute_MovesFiles_NeverOverwrites_AndReturnsTheVideos()
    {
        var library = Library(MediaLibraryKinds.Movies);
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "Rel"));
        File.WriteAllText(Path.Combine(staging, "Rel", "movie.mkv"), "video");
        File.WriteAllText(Path.Combine(staging, "Rel", "movie.srt"), "subs");
        var download = new MediaDownload { Kind = MediaKinds.Movie, Title = "Big Buck Bunny", Year = 2008 };
        var plan = MediaDownloadImporter.Plan(download, [new("Rel/movie.mkv", 5), new("Rel/movie.srt", 4)], staging, library, null);

        var landed = MediaDownloadImporter.Execute(plan);

        var target = Path.Combine(library.Path, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
        Assert.Equal([target], landed);
        Assert.Equal("video", File.ReadAllText(target));
        Assert.Equal("subs", File.ReadAllText(Path.Combine(library.Path, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).srt")));
        Assert.False(File.Exists(Path.Combine(staging, "Rel", "movie.mkv")));

        // A second copy of the same film gets its own name rather than replacing the first.
        File.WriteAllText(Path.Combine(staging, "Rel", "movie.mkv"), "again");
        var second = MediaDownloadImporter.Plan(download, [new("Rel/movie.mkv", 5)], staging, library, null);
        Assert.EndsWith("Big Buck Bunny (2008) (2).mkv", Assert.Single(second).Destination);
    }

    [Fact]
    public void SafeName_StripsWhatWindowsAndNtfsRefuse() =>
        Assert.Equal("Mission Impossible - Dead Reckoning", MediaDownloadImporter.SafeName("Mission: Impossible - Dead Reckoning?"));

    [Fact]
    public void VpnStatus_ReadsTheWatchersFile()
    {
        var path = Path.Combine(_root, "status.json");
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(MediaVpnStatus.Off, SystemdMediaDownloadStack.ReadStatus(path, now).State);

        File.WriteAllText(path, """{ "ip": "185.65.134.1", "city": "Stockholm", "region": "Stockholm", "country": "SE", "org": "AS39351 31173 Services AB" }""");
        var connected = SystemdMediaDownloadStack.ReadStatus(path, now);
        Assert.Equal((MediaVpnStatus.Connected, "185.65.134.1", "Stockholm, SE"), (connected.State, connected.Ip, connected.Location));

        File.WriteAllText(path, """{"ip":"185.65.134.1","country":"Sweden","city":"Stockholm","mullvad_exit_ip":true,"organization":"31173 Services AB"}""");
        Assert.Equal("31173 Services AB", SystemdMediaDownloadStack.ReadStatus(path, now).Org);

        File.WriteAllText(path, """{"error":"Checking the VPN...","connecting":true}""");
        Assert.Equal(MediaVpnStatus.Connecting, SystemdMediaDownloadStack.ReadStatus(path, now).State);

        File.WriteAllText(path, """{"error":"The VPN is not reaching the internet (timed out)"}""");
        Assert.Equal(MediaVpnStatus.Down, SystemdMediaDownloadStack.ReadStatus(path, now).State);

        // The watcher stopped writing: whatever it last said is no longer true.
        File.WriteAllText(path, """{ "ip": "185.65.134.1" }""");
        File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddMinutes(-10));
        Assert.Equal(MediaVpnStatus.Down, SystemdMediaDownloadStack.ReadStatus(path, now).State);
    }
}
