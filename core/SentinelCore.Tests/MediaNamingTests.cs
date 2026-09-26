using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The file and folder names below are real ones, from the library this feature was built for.
// Each is here because a simpler rule got it wrong.
public sealed class MediaNamingTests
{
    [Theory]
    [InlineData("17.Again.2009.1080p.BrRip.x264.YIFY", "17 Again", 2009)]
    [InlineData("17 Again (2009) [1080p]", "17 Again", 2009)]
    [InlineData("21.Jump.Street.2012.720p.BluRay.x264.YIFY", "21 Jump Street", 2012)]
    [InlineData("A Charlie Brown Christmas 1965 1080p BluRay x264 AAC - Ozlem", "A Charlie Brown Christmas", 1965)]
    [InlineData("A.Bugs.Life.1998.PROPER.1080p.BluRay.H264.AAC-LAMA[TGx]", "A Bugs Life", 1998)]
    [InlineData("Abigail (2024) [1080p] [WEBRip] [5.1] [YTS.MX]", "Abigail", 2024)]
    [InlineData("Alice In Wonderland (1951) BDrip 1080p ENG-ITA x264 - Nel Paese Delle Meraviglie", "Alice In Wonderland", 1951)]
    [InlineData("All.Dogs.Go.To.Heaven.1989.720p.BRRip.x264-x0r", "All Dogs Go To Heaven", 1989)]
    [InlineData("American.Made.2017.1080p.BluRay.6C.MkvCage", "American Made", 2017)]
    [InlineData("American.Psycho.2000.Remastered.1080p.BluRay.X264.AC3.Will1869", "American Psycho", 2000)]
    [InlineData("Miracle on 34th Street 1947 720p BluRay x264 AAC - Ozlem", "Miracle on 34th Street", 1947)]
    [InlineData("The Super Mario Bros Movie 2023 1080p WEBRip H265-CODY", "The Super Mario Bros Movie", 2023)]
    [InlineData("American Graffiti", "American Graffiti", null)]
    [InlineData("2012 (2009)", "2012", 2009)]
    [InlineData("1917", "1917", null)]
    [InlineData("Charlotte's Web (1973)", "Charlotte's Web", 1973)]
    [InlineData("Mr. Rogers Neighborhood", "Mr. Rogers Neighborhood", null)]
    [InlineData("www.torrenting.com - Fiddler.on.the.Roof.1971.1080p.BluRay.x264", "Fiddler on the Roof", 1971)]
    [InlineData("Its A Wonderful Life {1946} 1080p", "Its A Wonderful Life", 1946)]
    [InlineData("Jeremiah Johnson (Western 1972) 1080p", "Jeremiah Johnson", 1972)]
    [InlineData("01 John Wick Chapter 1 - Keanu Reeves 2014 Eng Subs 1080p [H264-mp4]", "John Wick Chapter 1 - Keanu Reeves", 2014)]
    [InlineData("Civil.War.2024 1080p", "Civil War", 2024)]
    [InlineData("Bobs.Burgers.S07.1080p.WEB-DL.DD5.1.H.264", "Bobs Burgers", null)]
    public void Titles_ComeOutOfReleaseNames(string name, string title, int? year)
    {
        var parsed = MediaNaming.ParseTitle(name);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData("Black Mirror (2011) Season 1-6 S01-S06 (1080p Mixed x265 HEVC 10bit EAC3 5.1 Silence)", "Black Mirror", 2011)]
    [InlineData("Bob's Burgers S01-S06 Collection [1080p WEB-DL HEVC_x265]", "Bob's Burgers", null)]
    [InlineData("Bobs.Burgers.S07.1080p.WEB-DL.DD5.1.H.264", "Bobs Burgers", null)]
    [InlineData("Gravity Falls 2012  Season 1 Complete 720p BluRay x264 [i_c]", "Gravity Falls", 2012)]
    [InlineData("Columbo S01-S10 (1968-)", "Columbo", 1968)]
    [InlineData("The Complete Donald Duck Collection", "The Complete Donald Duck Collection", null)]
    [InlineData("The Twilight Zone (1959) Season 1-5 S01-05 (1080p BluRay x265 HEVC 10bit AAC 2.0 ImE)", "The Twilight Zone", 1959)]
    [InlineData("Tulsa King (2024) Seaoson 2 S-2", "Tulsa King", 2024)]
    [InlineData("Tulsa King Season 3", "Tulsa King", null)]
    [InlineData("Seinfeld.Complete.Series-720p.WEBrip.AAC.EN-SUB.x264-[MULVAcoded]", "Seinfeld", null)]
    [InlineData("Amazing Stories 1985  Complete Seasons 1 and 2 DVDRip x264 [i_c]", "Amazing Stories", 1985)]
    [InlineData("The Simpsons S00-S09 (1989-) + Shorts (1987-1989)", "The Simpsons", 1989)]
    public void ShowFolders_CleanToTheShowName(string folder, string title, int? year)
    {
        var parsed = MediaNaming.ParseTitle(folder);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void FoldersForTheSameShow_ShareAKey()
    {
        Assert.Equal(
            MediaNaming.NormalizeKey(MediaNaming.ParseTitle("Bob's Burgers S01-S06 Collection [1080p WEB-DL HEVC_x265]").Title),
            MediaNaming.NormalizeKey(MediaNaming.ParseTitle("Bobs.Burgers.S07.1080p.WEB-DL.DD5.1.H.264").Title));
        Assert.Equal(
            MediaNaming.NormalizeKey(MediaNaming.ParseTitle("Tulsa King (2022) Season 1 S01 (1080p AMZN WEB-DL x265 HEVC 10bit EAC3 5.1 t3nzin)").Title),
            MediaNaming.NormalizeKey(MediaNaming.ParseTitle("Tulsa King Season 3").Title));
    }

    [Theory]
    [InlineData("Black Mirror (2011) - S01E01 - The National Anthem (1080p BluRay x265 Silence)", null, 1, 1, "The National Anthem")]
    [InlineData("BattleBots.2015.S03E05.WEB.x264-TBS[eztv]", null, 3, 5, null)]
    [InlineData("BattleBots.2015.S03E01.REAL.WEB.x264-TBS[ettv]", null, 3, 1, null)]
    [InlineData("Garfield And Friends S02E02 Rip Van Kitty Grabbity The Big Catnap 1080p WEB-DL AAC2 0 x264-NTb[TGx]", null, 2, 2, "Rip Van Kitty Grabbity The Big Catnap")]
    [InlineData("Seinfeld 2x03 The Pony Remark", null, 2, 3, "The Pony Remark")]
    [InlineData("Episode 7", 4, 4, 7, null)]
    [InlineData("Donald Duck 1937 Donald's Ostrich", 1, 1, null, null)]
    [InlineData("South Park [1x05] An Elephant Makes Love To A Pig", null, 1, 5, "An Elephant Makes Love To A Pig")]
    [InlineData("SpongeBob SquarePants - s1e01b - Reef Blower [Fester1500]", null, 1, 1, "Reef Blower")]
    [InlineData("S01 E01.avi", null, 1, 1, null)]
    [InlineData("The Grand Tour S02E03 Bah humbug-atti", null, 2, 3, "Bah humbug-atti")]
    [InlineData("Blaze.and.the.Monster.Machines.S01E01E02.Blaze.of.Glory.WEBRip.x264.AAC", null, 1, 1, "Blaze of Glory")]
    public void Episodes_ReadSeasonNumberAndTitle(string name, int? folderSeason, int? season, int? episode, string? title)
    {
        var parsed = MediaNaming.ParseEpisode(name, folderSeason);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(title, parsed.Title);
    }

    [Theory]
    [InlineData("Star Wars The Clone Wars S01E01 Ambush", "Star Wars The Clone Wars")]
    [InlineData("Mister Rogers' Neighborhood - 10x02 - Starting School (x264,AAC)", "Mister Rogers' Neighborhood")]
    [InlineData("S00E01.Good.News.Bad.News-Alternative.pilot", null)]
    public void Episodes_CarryTheShowNameTheFileUses(string name, string? show) =>
        Assert.Equal(show, MediaNaming.ParseEpisode(name, null).ShowName?.Title);

    [Theory]
    [InlineData("Mickey Mouse 1928 Plane Crazy", "Mickey Mouse", "Plane Crazy (1928)")]
    [InlineData("Donald Duck 1937 Donald's Ostrich", "Disney - Donald Duck", "Donald's Ostrich (1937)")]
    [InlineData("Schoolhouse Rock - America Rock - Elbow Room", "schoolhouse rock", "America Rock - Elbow Room")]
    [InlineData("01 - Muddy Puddles [STEiNO]", "Peppa Pig", "Muddy Puddles")]
    [InlineData("Minnies Pet Salon", "Mickey Mouse Clubhouse", "Minnies Pet Salon")]
    public void UnnumberedEpisodes_DropTheShowName(string name, string show, string title) =>
        Assert.Equal(title, MediaNaming.UnnumberedEpisodeTitle(name, show));

    [Fact]
    public void SearchCandidates_TryTheWholeTitleThenTheHalfBeforeADash() =>
        Assert.Equal(["John Wick Chapter 1 - Keanu Reeves", "John Wick Chapter 1"], MediaNaming.SearchCandidates("John Wick Chapter 1 - Keanu Reeves"));

    [Theory]
    [InlineData("Season 1 (1080p Mixed)", 1)]
    [InlineData("Season 12", 12)]
    [InlineData("S03", 3)]
    [InlineData("Specials", 0)]
    [InlineData("Extras", null)]
    [InlineData("Seasonal Favourites", null)]
    public void SeasonFolders(string folder, int? season) => Assert.Equal(season, MediaNaming.ParseSeasonFolder(folder));

    [Theory]
    [InlineData("The Simpsons", "simpsons, the")]
    [InlineData("A Bugs Life", "bugs life, a")]
    [InlineData("Anastasia", "anastasia")]
    public void SortTitles_MoveArticlesToTheEnd(string title, string sort) => Assert.Equal(sort, MediaNaming.SortTitle(title));

    [Theory]
    [InlineData("Movie.Sample.mkv", true)]
    [InlineData("sample.mp4", true)]
    [InlineData("Movie-trailer.mp4", true)]
    [InlineData("The Sampler (2019).mkv", false)]
    [InlineData("Movie.mkv", false)]
    public void SamplesAndTrailers_AreSkipped(string file, bool skipped) => Assert.Equal(skipped, MediaNaming.IsSkippedFile(file));

    [Fact]
    public void StableIds_DependOnlyOnLibraryAndPath()
    {
        var library = Guid.NewGuid();
        Assert.Equal(MediaNaming.StableId(library, "a/b.mkv"), MediaNaming.StableId(library, "a/b.mkv"));
        Assert.NotEqual(MediaNaming.StableId(library, "a/b.mkv"), MediaNaming.StableId(library, "a/c.mkv"));
        Assert.NotEqual(MediaNaming.StableId(library, "a/b.mkv"), MediaNaming.StableId(Guid.NewGuid(), "a/b.mkv"));
    }

    // Not a test of anything by itself: with MEDIA_DUMP_ROOT pointing at a copy of a real library
    // layout (empty files are enough), writes what the scanner makes of it to MEDIA_DUMP_OUT, so a
    // person can read the whole shelf and spot the names this parser still gets wrong.
    [Fact]
    public void DumpWhatTheScannerMakesOfARealLibrary()
    {
        var root = Environment.GetEnvironmentVariable("MEDIA_DUMP_ROOT");
        var output = Environment.GetEnvironmentVariable("MEDIA_DUMP_OUT");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(output)) return;

        var lines = new List<string>();
        var movies = MediaScanner.WalkMovies(new MediaLibrary(Guid.NewGuid(), "Movies", MediaLibraryKinds.Movies, Path.Combine(root, "Movies"), DateTimeOffset.UtcNow));
        lines.Add($"== MOVIES ({movies.Count})");
        foreach (var movie in movies.OrderBy(m => m.SortTitle))
            lines.Add($"{movie.Title} ({movie.Year?.ToString() ?? "----"})    <- {Path.GetRelativePath(root, movie.Path)}");

        var tv = MediaScanner.WalkShows(new MediaLibrary(Guid.NewGuid(), "TV", MediaLibraryKinds.Shows, Path.Combine(root, "TV Shows"), DateTimeOffset.UtcNow));
        lines.Add($"== SHOWS ({tv.Count(i => i.Kind == MediaKinds.Show)} shows, {tv.Count(i => i.Kind == MediaKinds.Episode)} episodes)");
        foreach (var show in tv.Where(i => i.Kind == MediaKinds.Show).OrderBy(s => s.SortTitle))
        {
            var episodes = tv.Where(e => e.ParentId == show.Id).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            lines.Add($"{show.Title} ({show.Year?.ToString() ?? "----"}) - {episodes.Count} episodes, seasons {string.Join(",", episodes.Select(e => e.SeasonNumber).Distinct())}");
            foreach (var episode in episodes.Take(4))
                lines.Add($"    S{episode.SeasonNumber}E{episode.EpisodeNumber} {episode.Title}    <- {Path.GetFileName(episode.Path)}");
        }
        File.WriteAllLines(output, lines);
    }
}
