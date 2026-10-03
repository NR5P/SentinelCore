using Microsoft.Data.Sqlite;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The frame-order check (MediaItem.TimestampsInDecodeOrder) came after the library was probed, so
// a media.db from before it sends its copyable films back through the probe - once.
public sealed class MediaFrameOrderBackfillTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"media-frame-order-{Guid.NewGuid():N}");

    public MediaFrameOrderBackfillTests() => Directory.CreateDirectory(_dataDirectory);

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MediaItem Probed(string file, string video, bool flagged = false) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = Guid.NewGuid(),
        Kind = MediaKinds.Movie,
        Title = file,
        SortTitle = file,
        Path = "/films/" + file,
        Container = Path.GetExtension(file).TrimStart('.'),
        VideoCodec = video,
        TimestampsInDecodeOrder = flagged,
        AddedAt = DateTimeOffset.UtcNow,
        ProbedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task AnOlderLibrary_IsReprobedOnce_ForFilesWhoseVideoIsCopied()
    {
        var et = Probed("et.mp4", "h264");
        var rambo = Probed("rambo.mkv", "hevc");
        var xvid = Probed("wargames.avi", "mpeg4");
        var avi264 = Probed("old.avi", "h264");
        using (var store = new SqliteMediaStore(_dataDirectory))
            await store.UpsertItemsAsync([et, rambo, xvid, avi264], Ct);
        // A media.db from before the check.
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dataDirectory, "media.db")};Pooling=False"))
        {
            connection.Open();
            using var forget = connection.CreateCommand();
            forget.CommandText = "DELETE FROM MediaState WHERE Key = 'probe.frameOrderChecked'";
            forget.ExecuteNonQuery();
        }

        using (var store = new SqliteMediaStore(_dataDirectory))
        {
            var queued = (await store.ItemsNeedingProbeAsync(10, Ct)).Select(i => i.Id).Order().ToList();
            Assert.Equal(new[] { et.Id, rambo.Id }.Order(), queued);
            // The facts already known stay usable until the probe comes round.
            Assert.Equal("h264", (await store.GetItemAsync(et.Id, Ct))!.VideoCodec);

            et.ProbedAt = DateTimeOffset.UtcNow;
            et.TimestampsInDecodeOrder = true;
            await store.UpdateProbeAsync(et, Ct);
            Assert.True((await store.GetItemAsync(et.Id, Ct))!.TimestampsInDecodeOrder);
        }

        // Opening it again does not queue anything a second time.
        using (var store = new SqliteMediaStore(_dataDirectory))
            Assert.Equal([rambo.Id], (await store.ItemsNeedingProbeAsync(10, Ct)).Select(i => i.Id));
    }

    [Fact]
    public async Task ANewLibrary_IsNotReprobed()
    {
        using (var store = new SqliteMediaStore(_dataDirectory))
            await store.UpsertItemsAsync([Probed("film.mp4", "h264")], Ct);
        using var reopened = new SqliteMediaStore(_dataDirectory);
        Assert.Empty(await reopened.ItemsNeedingProbeAsync(10, Ct));
    }
}
