extern alias WebHost;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Lightning on the radar map: reading GOES-19 Lightning Mapper files (a real one, captured
// 2026-10-14, is in Fixtures), reading them a block at a time over Range requests, the bucket
// listing, and what the map is sent - including "only what's new since last time".
public sealed class LightningServiceTests
{
    private const string FixtureName = "OR_GLM-L2-LCFA_G19_s20262870501400_e20262870502000_c20262870502024.nc";
    private const string FixtureKey = "GLM-L2-LCFA/2026/287/05/" + FixtureName;
    private static readonly DateTimeOffset FixtureStart = new(2026, 10, 14, 5, 1, 40, TimeSpan.Zero);

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName));

    [Fact]
    public void File_names_carry_start_and_end_to_the_tenth_of_a_second()
    {
        Assert.True(LightningService.TryParseFileTimes(FixtureKey, out var start, out var end));
        Assert.Equal(FixtureStart, start);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 5, 2, 0, TimeSpan.Zero), end);

        Assert.True(LightningService.TryParseFileTimes("OR_GLM-L2-LCFA_G19_s20260011200015_e20260011200215_c20260011200230.nc", out start, out _));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 12, 0, 1, 500, TimeSpan.Zero), start);

        Assert.False(LightningService.TryParseFileTimes("GLM-L2-LCFA/2026/287/05/readme.txt", out _, out _));
    }

    [Fact]
    public void Hour_prefix_uses_day_of_year()
    {
        Assert.Equal("GLM-L2-LCFA/2026/287/05/", LightningService.HourPrefix(new DateTimeOffset(2026, 10, 14, 5, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Bucket_listing_yields_keys_sizes_and_times()
    {
        var entries = LightningService.ParseListing(Listing((FixtureKey, 361939), ("GLM-L2-LCFA/2026/287/05/junk.txt", 10)));
        var entry = Assert.Single(entries);
        Assert.Equal(FixtureKey, entry.Key);
        Assert.Equal(361939, entry.Size);
        Assert.Equal(FixtureStart, entry.Start);
    }

    [Fact]
    public void The_box_wraps_round_the_world()
    {
        var box = new LightningBox(170, -10, 190, 10);
        Assert.True(box.Contains(0, -175));
        Assert.True(box.Contains(0, 175));
        Assert.False(box.Contains(0, 160));
        Assert.False(box.Contains(20, 175));
        Assert.True(new LightningBox(-400, -90, 400, 90).Contains(45, 10));
    }

    [Fact]
    public void A_real_file_yields_flashes_inside_its_twenty_seconds()
    {
        using var stream = new MemoryStream(Fixture());
        var flashes = LightningService.ParseFlashes(stream, FixtureStart);

        Assert.True(flashes.Count > 50, $"only {flashes.Count} flashes");
        Assert.All(flashes, f =>
        {
            Assert.InRange(f.Latitude, -60f, 60f);
            Assert.InRange(f.Longitude, -160f, 10f);
            // The time offset's packing starts 5 s early; real flashes sit inside the file's span.
            Assert.InRange(f.Time, FixtureStart.AddSeconds(-5), FixtureStart.AddSeconds(21));
            Assert.True(f.EnergyFemtojoules > 0);
        });
        Assert.Contains(flashes, f => f.Time >= FixtureStart);
    }

    [Fact]
    public void Range_reads_fetch_a_fraction_of_the_file_and_read_the_same_flashes()
    {
        var bytes = Fixture();
        var handler = new Bucket();
        handler.Files[FixtureKey] = bytes;
        using var client = new HttpClient(handler);
        using var remote = new HttpRangeStream(client, $"{LightningService.BucketUrl}/{FixtureKey}", bytes.Length);

        var overHttp = LightningService.ParseFlashes(remote, FixtureStart);
        using var local = new MemoryStream(bytes);
        var fromDisk = LightningService.ParseFlashes(local, FixtureStart);

        Assert.Equal(fromDisk, overHttp);
        Assert.True(remote.BytesFetched < bytes.Length * 0.75, $"fetched {remote.BytesFetched} of {bytes.Length}");
        Assert.All(handler.Ranges, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task The_map_gets_everything_once_then_only_whats_new()
    {
        var handler = new Bucket();
        handler.Files[FixtureKey] = Fixture();
        handler.Listings["GLM-L2-LCFA/2026/287/04/"] = Listing();
        handler.Listings["GLM-L2-LCFA/2026/287/05/"] = Listing((FixtureKey, handler.Files[FixtureKey].Length));
        var service = new LightningService(new Factory(handler), NullLogger<LightningService>.Instance, new FakeClock(FixtureStart.AddMinutes(10)));

        var before = service.GetFlashes(null, null, null, null, null, null);
        Assert.Equal("loading", before.State);
        Assert.Empty(before.Flashes);

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        var first = service.GetFlashes(-180, -90, 180, 90, null, null);
        Assert.Equal("ready", first.State);
        Assert.False(first.Incremental);
        Assert.True(first.Flashes.Count > 50);
        Assert.Equal(FixtureStart, first.CoverageStart);
        // [lat, lon, seconds after origin, energy], oldest first.
        var seconds = first.Flashes.Select(f => f[2]).ToList();
        Assert.Equal(seconds.Order(), seconds);
        var firstTime = DateTimeOffset.FromUnixTimeMilliseconds(first.Origin).AddSeconds(seconds[0]);
        Assert.InRange(firstTime, FixtureStart.AddSeconds(-5), FixtureStart.AddSeconds(21));

        var again = service.GetFlashes(-180, -90, 180, 90, first.Epoch, first.Sequence);
        Assert.True(again.Incremental);
        Assert.Empty(again.Flashes);

        // From before a restart (a different epoch) it starts over.
        var stale = service.GetFlashes(-180, -90, 180, 90, "oldepoch", first.Sequence);
        Assert.False(stale.Incremental);
        Assert.Equal(first.Flashes.Count, stale.Flashes.Count);

        // A box with nothing in it (the middle of the South Pacific).
        Assert.Empty(service.GetFlashes(-140, -50, -139, -49, null, null).Flashes);

        // A second round reads nothing again.
        var requests = handler.Ranges.Count;
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(requests, handler.Ranges.Count);
    }

    [Fact]
    public async Task Files_older_than_the_loop_are_not_read()
    {
        var handler = new Bucket();
        handler.Files[FixtureKey] = Fixture();
        handler.Listings["GLM-L2-LCFA/2026/287/05/"] = Listing((FixtureKey, handler.Files[FixtureKey].Length));
        handler.Listings["GLM-L2-LCFA/2026/287/06/"] = Listing();
        handler.Listings["GLM-L2-LCFA/2026/287/07/"] = Listing();
        var service = new LightningService(new Factory(handler), NullLogger<LightningService>.Instance, new FakeClock(FixtureStart.AddHours(2)));

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Empty(handler.Ranges);
        Assert.Empty(service.GetFlashes(null, null, null, null, null, null).Flashes);
    }

    private static string Listing(params (string Key, long Size)[] files)
    {
        var body = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Name>noaa-goes19</Name>""");
        foreach (var (key, size) in files)
            body.Append($"<Contents><Key>{key}</Key><LastModified>2026-10-14T05:02:30.000Z</LastModified><Size>{size}</Size><StorageClass>STANDARD</StorageClass></Contents>");
        return body.Append("<IsTruncated>false</IsTruncated></ListBucketResult>").ToString();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // A pretend S3 bucket: listings by prefix, files served by Range, both sync and async.
    private sealed class Bucket : HttpMessageHandler
    {
        public Dictionary<string, string> Listings { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (query["list-type"] == "2")
            {
                var prefix = query["prefix"] ?? "";
                return Listings.TryGetValue(prefix, out var xml)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Listing(), Encoding.UTF8, "application/xml") };
            }

            var key = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (!Files.TryGetValue(key, out var bytes)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            lock (Ranges) Ranges.Add(request.Headers.Range);
            var range = request.Headers.Range?.Ranges.Single();
            if (range is null) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var from = (int)range.From!.Value;
            var to = (int)Math.Min(range.To!.Value, bytes.Length - 1);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[from..(to + 1)]) };
        }
    }
}
