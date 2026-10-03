using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// `lookup?kind=any` - what a library search that found nothing offers to download.
public sealed class MediaLookupMergeTests
{
    private static TmdbClient.SearchResult R(int id, string title) => new(id, title, 2020, null, null);

    private static List<string> Merge(string query, IReadOnlyList<TmdbClient.SearchResult> movies, IReadOnlyList<TmdbClient.SearchResult> shows) =>
        MediaDownloadEndpoints.MergeLookups(query, [MediaKinds.Movie, MediaKinds.Show], [movies, shows])
            .Select(x => $"{x.Kind}:{x.Result.TmdbId}")
            .ToList();

    [Fact]
    public void KindsTakeTurnsInTmdbsOrder()
    {
        var merged = Merge("dune", [R(1, "Dune Part Two"), R(2, "Dune Drifter"), R(3, "Jodorowsky's Dune")], [R(10, "Dune: Prophecy")]);
        Assert.Equal(["movie:1", "show:10", "movie:2", "movie:3"], merged);
    }

    [Fact]
    public void AnExactTitleComesFirstWhicheverKindItIs()
    {
        var merged = Merge("bluey", [R(1, "Bluey: The Movie"), R(2, "Bluey Christmas")], [R(10, "Bluey"), R(11, "Bluey Minisodes")]);
        Assert.Equal(["show:10", "movie:1", "movie:2", "show:11"], merged);
    }

    [Theory]
    [InlineData("tom and jerry", "Tom & Jerry")]
    [InlineData("office", "The Office")]
    [InlineData("spiderman", "Spider-Man")]
    public void ExactMeansTheSameWordsNotTheSamePunctuation(string typed, string title)
    {
        var merged = Merge(typed, [R(1, "Something Else"), R(2, title)], []);
        Assert.Equal("movie:2", merged[0]);
    }

    [Fact]
    public void NothingFoundIsAnEmptyList()
    {
        Assert.Empty(Merge("zzz", [], []));
    }
}
