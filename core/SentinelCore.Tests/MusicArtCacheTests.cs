using SentinelCore.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

public sealed class MusicArtCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"music-art-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // An album page asks for the same new size once per track row, all at once. They used to share
    // one temporary file and fail with "being used by another process" (a 500 on the Apple TV).
    [Fact]
    public async Task ManyRequestsForTheSameNewSize_AllGetAPicture()
    {
        Directory.CreateDirectory(_root);
        var cover = Path.Combine(_root, "cover.jpg");
        using (var image = new Image<Rgb24>(900, 900, new Rgb24(200, 120, 40))) await image.SaveAsJpegAsync(cover, TestContext.Current.CancellationToken);
        var cache = new MusicArtCache(Path.Combine(_root, "cache"));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => cache.SizedAsync(cover, LocalMediaFileSystem.Instance, 128, TestContext.Current.CancellationToken))));

        Assert.All(results, path => Assert.True(File.Exists(path), path));
        var sized = Assert.Single(results.Distinct(), p => p != cover);
        using var resized = await Image.LoadAsync(sized, TestContext.Current.CancellationToken);
        Assert.Equal(128, resized.Width);
        Assert.Empty(Directory.EnumerateFiles(cache.SizedRoot, "*.tmp"));
    }
}
