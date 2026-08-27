extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The Events tab is the landing tab, it asks for fifty rows, and every row used to render the
// camera's own frame into a card a few hundred pixels wide. On this install that meant 3840x2160
// and 2560x1920 snapshots averaging 1,277 KB - tens of megabytes per page load, and fifty
// multi-megapixel JPEG decodes on the main thread, which is the thread the live tiles append
// their video on.
//
// These cover the two halves of the fix: a card-sized copy exists and is reused, and the original
// is still what the lightbox and every other caller get.
public sealed class EventImageThumbnailTests : IDisposable
{
    private readonly string _root;

    public EventImageThumbnailTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"thumb-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private EventImageThumbnails CreateThumbnails()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CamerasDataDirectory"] = _root })
            .Build();
        return new EventImageThumbnails(
            configuration, new StubEnvironment(_root), NullLogger<EventImageThumbnails>.Instance);
    }

    // Deliberately noisy rather than a flat colour: a solid image compresses to almost nothing at
    // any size, which would make "the copy is smaller" true for uninteresting reasons.
    private string WriteJpeg(string name, int width, int height)
    {
        var path = Path.Combine(_root, name);
        using var image = new Image<Rgb24>(width, height);
        image.Mutate(context => context.ProcessPixelRowsAsVector4((row, point) =>
        {
            for (var x = 0; x < row.Length; x++)
            {
                row[x].X = ((x * 7) % 256) / 255f;
                row[x].Y = ((point.Y * 13) % 256) / 255f;
                row[x].Z = ((x * point.Y) % 256) / 255f;
            }
        }));
        image.SaveAsJpeg(path, new JpegEncoder { Quality = 92 });
        return path;
    }

    [Fact]
    public async Task ACameraSizedFrame_IsCopiedDownToSomethingACardCanUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = WriteJpeg("full.jpg", 3840, 2160);
        var thumbnails = CreateThumbnails();

        var cached = await thumbnails.GetOrCreateAsync(source, ct);

        Assert.NotNull(cached);
        Assert.NotEqual(source, cached);

        var info = await Image.IdentifyAsync(cached!, ct);
        Assert.Equal(EventImageThumbnails.MaxEdge, info.Width);
        Assert.True(info.Height < 2160, $"expected the height to come down too, got {info.Height}");

        // The point of the exercise: far fewer bytes over the wire, and far fewer pixels to decode.
        var originalBytes = new FileInfo(source).Length;
        var cachedBytes = new FileInfo(cached!).Length;
        Assert.True(cachedBytes * 4 < originalBytes,
            $"expected a large saving, got {cachedBytes} from {originalBytes}");
    }

    // A published browser webcam or an older camera can already be smaller than a card. Blowing
    // that up to 960 and re-encoding would spend bytes and quality to gain nothing.
    [Fact]
    public async Task AFrameSmallerThanACard_IsNotBlownUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = WriteJpeg("small.jpg", 640, 480);

        var cached = await CreateThumbnails().GetOrCreateAsync(source, ct);

        Assert.NotNull(cached);
        var info = await Image.IdentifyAsync(cached!, ct);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
    }

    [Fact]
    public async Task TheCopyIsMadeOnce_AndReusedAfterwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = WriteJpeg("full.jpg", 2560, 1920);
        var thumbnails = CreateThumbnails();

        var first = await thumbnails.GetOrCreateAsync(source, ct);
        Assert.NotNull(first);
        var writtenAt = File.GetLastWriteTimeUtc(first!);

        var second = await thumbnails.GetOrCreateAsync(source, ct);

        Assert.Equal(first, second);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second!));
    }

    // Fifty cards arrive at once, all for the same tab. They must not all decode the same frame -
    // and, more importantly, none may be served a half-written file.
    [Fact]
    public async Task ManyRequestsForTheSameFrame_ProduceOneUsableCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = WriteJpeg("full.jpg", 2560, 1920);
        var thumbnails = CreateThumbnails();

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => thumbnails.GetOrCreateAsync(source, ct)));

        Assert.All(results, path => Assert.Equal(results[0], path));
        var info = await Image.IdentifyAsync(results[0]!, ct);
        Assert.Equal(EventImageThumbnails.MaxEdge, info.Width);
    }

    // The name is content-addressed, so a snapshot replaced in place must not keep serving the
    // picture it used to be.
    [Fact]
    public async Task AReplacedSnapshot_IsNotServedFromTheOldCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = WriteJpeg("full.jpg", 2560, 1920);
        var thumbnails = CreateThumbnails();
        var first = await thumbnails.GetOrCreateAsync(source, ct);

        File.Delete(source);
        // Deliberately under MaxEdge, so the copy keeps these dimensions and the assertion below
        // distinguishes the new content from the old rather than just reading back the cap.
        WriteJpeg("full.jpg", 800, 600);
        // Same path, different content - and a filesystem whose timestamps are coarse would
        // otherwise let the old key stand.
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(5));

        var second = await thumbnails.GetOrCreateAsync(source, ct);

        Assert.NotEqual(first, second);
        var info = await Image.IdentifyAsync(second!, ct);
        Assert.Equal(800, info.Width);
    }

    // A card showing the full frame is worse than a card showing a small one. A card showing
    // nothing is worse than both, so an undecodable file falls back rather than failing.
    [Fact]
    public async Task AFileThatIsNotAnImage_FallsBackInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_root, "corrupt.jpg");
        await File.WriteAllTextAsync(source, "this is not a JPEG", ct);

        Assert.Null(await CreateThumbnails().GetOrCreateAsync(source, ct));
        Assert.Null(await CreateThumbnails().GetOrCreateAsync(Path.Combine(_root, "absent.jpg"), ct));
    }

    // End to end, over the endpoint the Events list actually calls.
    [Fact]
    public async Task TheEndpointServesTheCardCopyOnlyWhenAsked_AndLetsTheBrowserKeepIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var dataDirectory = Path.Combine(_root, "app-data");
        Directory.CreateDirectory(dataDirectory);
        var settingsPath = Path.Combine(dataDirectory, "appsettings.json");
        await File.WriteAllTextAsync(settingsPath, "{}", ct);

        using var factory = new WebApplicationFactory<WebProgram>()
            .WithWebHostBuilder(builder => builder
                .UseSetting("MainAppSettingsPath", settingsPath)
                .UseSetting("CamerasDataDirectory", dataDirectory)
                .UseSetting("Bridge:DataDirectory", dataDirectory)
                .UseSetting("InternetExposed:Enabled", "false"));
        using var client = factory.CreateClient();

        var cameraId = await AddCameraAsync(client, ct);
        var imagePath = WriteJpeg("event.jpg", 3840, 2160);

        Guid triggerId;
        using (var scope = factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<ITriggerEventStore>();
            var recorded = await events.AddAsync(cameraId, imagePath, 0.4, null, TriggerConfirmation.Confirmed, ct);
            triggerId = recorded.Id;
        }

        using var full = await client.GetAsync($"/api/triggers/{triggerId:D}/image", ct);
        using var card = await client.GetAsync($"/api/triggers/{triggerId:D}/image?size=card", ct);

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);

        var fullBytes = (await full.Content.ReadAsByteArrayAsync(ct)).Length;
        var cardBytes = (await card.Content.ReadAsByteArrayAsync(ct)).Length;

        // The original is served byte for byte - the lightbox, and anything that treats this as
        // evidence, must not quietly receive a downscaled picture.
        Assert.Equal(new FileInfo(imagePath).Length, fullBytes);
        Assert.True(cardBytes * 4 < fullBytes, $"expected a much smaller card copy, got {cardBytes} from {fullBytes}");

        // Cacheable, and private: these are authenticated, and Internet Exposed Mode can put a
        // Cloudflare Tunnel in this path. A shared cache holding camera snapshots must not happen.
        var cacheControl = card.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.Private, "camera snapshots must never be stored by a shared cache");
        Assert.NotNull(cacheControl.MaxAge);
        Assert.True(cacheControl.MaxAge > TimeSpan.FromHours(1));
    }

    private static async Task<Guid> AddCameraAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/cameras",
            new { Name = "Front of office", StreamUrl = "rtsp://10.0.0.4/main", IncludeAudio = false }, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        return created.GetProperty("id").GetGuid();
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
