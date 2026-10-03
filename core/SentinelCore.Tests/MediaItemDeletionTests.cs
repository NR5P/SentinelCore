using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// What deleting one movie takes with it - never a neighbour's files.
public sealed class MediaItemDeletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"media-delete-{Guid.NewGuid():N}");

    public MediaItemDeletionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void AFolderOfItsOwn_GoesWhole_SamplesAndArtIncluded()
    {
        var video = Touch("Dune (2021)", "Dune (2021).mkv");
        Touch("Dune (2021)", "Sample", "sample.mkv");
        Touch("Dune (2021)", "folder.jpg");

        var (folder, files) = MediaItemDeletion.Plan(video, _root);
        Assert.Equal(Path.Combine(_root, "Dune (2021)"), folder);
        Assert.Empty(files);
    }

    [Fact]
    public void ASharedFolder_LosesOnlyTheVideoAndItsSidecars()
    {
        var video = Touch("Dune (2021).mkv");
        Touch("Dune (2021).en.srt");
        Touch("Dune (2021)-poster.jpg");
        Touch("Dune (2021) Part Two.mkv");
        Touch("Dune (2021) Part Two.srt");

        var (folder, files) = MediaItemDeletion.Plan(video, _root);
        Assert.Null(folder);
        Assert.Equal(
            new[] { "Dune (2021)-poster.jpg", "Dune (2021).en.srt", "Dune (2021).mkv" },
            files.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ACollectionFolder_WithOtherMoviesInside_IsKept()
    {
        var video = Touch("Transformers", "Transformers (2007).mkv");
        Touch("Transformers", "Revenge of the Fallen (2009)", "Revenge of the Fallen (2009).mkv");

        var (folder, files) = MediaItemDeletion.Plan(video, _root);
        Assert.Null(folder);
        Assert.Equal([video], files);
    }

    [Fact]
    public void OnlyLocalMoviesInsideTheirLibrary_CanBeDeleted()
    {
        var library = new MediaLibrary(Guid.NewGuid(), "Movies", MediaLibraryKinds.Movies, _root, DateTimeOffset.UtcNow);
        var movie = new MediaItem { Kind = MediaKinds.Movie, Path = Path.Combine(_root, "A.mkv") };
        Assert.Null(MediaItemDeletion.Problem(movie, library));
        Assert.NotNull(MediaItemDeletion.Problem(new MediaItem { Kind = MediaKinds.Show, Path = _root }, library));
        Assert.NotNull(MediaItemDeletion.Problem(new MediaItem { Kind = MediaKinds.Movie, Path = "/elsewhere/A.mkv" }, library));
        Assert.NotNull(MediaItemDeletion.Problem(new MediaItem { Kind = MediaKinds.Movie, Path = _root }, library));
        Assert.NotNull(MediaItemDeletion.Problem(movie, null));
    }
}
