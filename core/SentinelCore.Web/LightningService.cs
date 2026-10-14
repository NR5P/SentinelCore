using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PureHDF;

namespace SentinelCore.Web;

// Lightning on the radar map: every flash the GOES-19 (GOES-East) satellite's Geostationary
// Lightning Mapper saw over the last loop window, with where and when, so the map can draw the
// strikes in step with the radar loop and fade them as they age.
//
// NOAA publishes the mapper's output to a public AWS bucket as one small NetCDF-4 (HDF5) file per
// 20 seconds, ~400-700 KB each. Almost all of a file is the raw pixel "events"; the flashes the map
// wants are a few kilobytes near the end. So each file is read through HttpRangeStream, which asks
// S3 only for the 16 KB blocks PureHDF actually touches - about a third of the file.
//
// Nothing is fetched until someone opens the radar map. The first request starts a backfill of the
// loop window (newest files first, so the latest strikes show up within seconds), then the worker
// keeps up every 20 seconds until nobody has asked for a while. What's held is a few megabytes in
// memory and is dropped as it ages out of the window.
//
// The mapper sees total lightning - in-cloud and cloud-to-ground alike - and places each flash at
// the energy-weighted middle of the ~8 km pixels it lit up, so a dot is good to a few miles.
public sealed class LightningService(
    IHttpClientFactory httpClientFactory,
    ILogger<LightningService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    public const string BucketUrl = "https://noaa-goes19.s3.amazonaws.com";
    public const string ProductPrefix = "GLM-L2-LCFA";

    // As long as the radar loop, so every frame has its lightning.
    public static readonly TimeSpan Window = WeatherMapService.LoopWindow;
    // How long the worker keeps fetching after the last time a browser asked.
    public static readonly TimeSpan DemandTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    // A file that failed is tried again on a later round, not every round.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(2);
    public const int Parallelism = 6;
    // Enough for the whole country in a busy storm season; past it the newest win.
    public const int MaximumFlashesPerAnswer = 40_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, LightningBatch> _batches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    // Hour folders that can't change any more, so each is listed once.
    private readonly ConcurrentDictionary<string, IReadOnlyList<GlmFileEntry>> _closedHours = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    // Changes on every restart, so a browser holding a sequence number from before knows to start over.
    private readonly string _epoch = Guid.NewGuid().ToString("N")[..8];
    private long _sequence;
    private long _lastDemandTicks;
    private int _pending;

    // ---------------- What the map asks for ----------------

    // Flashes inside the box, newest last. With `afterSequence` from the previous answer (and the
    // same epoch) only the files that arrived since are sent, so a map left open isn't re-sent
    // ninety minutes of lightning every half minute.
    public LightningAnswer GetFlashes(double? west, double? south, double? east, double? north, string? epoch, long? afterSequence)
    {
        Touch();
        var now = _clock.GetUtcNow();
        var incremental = epoch == _epoch && afterSequence is { } after && after <= Interlocked.Read(ref _sequence);
        var since = now - Window;

        var batches = _batches.Values
            .Where(b => b.End >= since && (!incremental || b.Sequence > afterSequence))
            .OrderBy(b => b.Start)
            .ToList();

        var box = new LightningBox(west ?? -180, south ?? -90, east ?? 180, north ?? 90);
        var picked = new List<LightningFlash>();
        foreach (var batch in batches)
            foreach (var flash in batch.Flashes)
                if (flash.Time >= since && box.Contains(flash.Latitude, flash.Longitude))
                    picked.Add(flash);

        var truncated = picked.Count > MaximumFlashesPerAnswer;
        if (truncated) picked = picked.OrderBy(f => f.Time).Skip(picked.Count - MaximumFlashesPerAnswer).ToList();
        else picked.Sort((a, b) => a.Time.CompareTo(b.Time));

        var all = _batches.Values.Where(b => b.End >= since).ToList();
        var origin = since.ToUnixTimeMilliseconds();
        return new LightningAnswer
        {
            State = Volatile.Read(ref _pending) > 0 || all.Count == 0 ? "loading" : "ready",
            Epoch = _epoch,
            Sequence = Interlocked.Read(ref _sequence),
            Incremental = incremental,
            CoverageStart = all.Count == 0 ? null : all.Min(b => b.Start),
            CoverageEnd = all.Count == 0 ? null : all.Max(b => b.End),
            Origin = origin,
            Truncated = truncated,
            // [latitude, longitude, seconds after origin (tenths), energy in femtojoules] - a
            // fraction of the size of the same thing as objects.
            Flashes = picked.Select(f => new[]
            {
                Math.Round(f.Latitude, 3),
                Math.Round(f.Longitude, 3),
                Math.Round((f.Time.ToUnixTimeMilliseconds() - origin) / 1000.0, 1),
                Math.Round(f.EnergyFemtojoules)
            }).ToList()
        };
    }

    private void Touch()
    {
        var wasIdle = !IsWanted();
        Interlocked.Exchange(ref _lastDemandTicks, _clock.GetUtcNow().UtcTicks);
        if (wasIdle && _wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private bool IsWanted() =>
        _clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastDemandTicks) < DemandTimeout.Ticks;

    // ---------------- The worker ----------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsWanted())
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation(ex, "Couldn't update the lightning data");
                }
            }
            Prune();
            try
            {
                await _wake.WaitAsync(IsWanted() ? PollInterval : TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    // One round: list the hour folders covering the window, read every file not yet held, newest
    // first. Public for the tests.
    public async Task RefreshAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var since = now - Window;
        var files = new List<GlmFileEntry>();
        for (var hour = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, 0, 0, TimeSpan.Zero); hour <= now; hour = hour.AddHours(1))
            files.AddRange(await ListHourAsync(hour, now, ct));

        var wanted = files
            .Where(f => f.End >= since && !_batches.ContainsKey(f.Key))
            .Where(f => !_failed.TryGetValue(f.Key, out var failedAt) || now - failedAt > RetryAfter)
            .OrderByDescending(f => f.Start)
            .ToList();
        if (wanted.Count == 0) return;

        Interlocked.Exchange(ref _pending, wanted.Count);
        try
        {
            await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (file, token) =>
            {
                try
                {
                    var flashes = await Task.Run(() => ReadFile(file), token);
                    _batches[file.Key] = new LightningBatch(file.Start, file.End, Interlocked.Increment(ref _sequence), flashes);
                    _failed.TryRemove(file.Key, out _);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _failed[file.Key] = _clock.GetUtcNow();
                    logger.LogDebug(ex, "Couldn't read lightning file {Key}", file.Key);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _pending, 0);
        }
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - Window - TimeSpan.FromMinutes(5);
        foreach (var (key, batch) in _batches)
            if (batch.End < cutoff) _batches.TryRemove(key, out _);
        foreach (var (key, at) in _failed)
            if (at < cutoff) _failed.TryRemove(key, out _);
        foreach (var key in _closedHours.Keys)
            if (DateTimeOffset.TryParseExact(key, "yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var hour) && hour.AddHours(1) < cutoff)
                _closedHours.TryRemove(key, out _);
    }

    private async Task<IReadOnlyList<GlmFileEntry>> ListHourAsync(DateTimeOffset hour, DateTimeOffset now, CancellationToken ct)
    {
        var cacheKey = hour.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        if (_closedHours.TryGetValue(cacheKey, out var cached)) return cached;

        var prefix = HourPrefix(hour);
        var client = httpClientFactory.CreateClient(nameof(LightningService));
        var xml = await client.GetStringAsync($"{BucketUrl}/?list-type=2&prefix={Uri.EscapeDataString(prefix)}", ct);
        var entries = ParseListing(xml);
        // Files land about 20 seconds after they end; a few minutes past the hour nothing more will.
        if (now - hour > TimeSpan.FromMinutes(65)) _closedHours[cacheKey] = entries;
        return entries;
    }

    private IReadOnlyList<LightningFlash> ReadFile(GlmFileEntry file)
    {
        var client = httpClientFactory.CreateClient(nameof(LightningService));
        using var stream = new HttpRangeStream(client, $"{BucketUrl}/{file.Key}", file.Size);
        return ParseFlashes(stream, file.Start);
    }

    // ---------------- Reading the files ----------------

    public static string HourPrefix(DateTimeOffset hour) =>
        $"{ProductPrefix}/{hour.Year:D4}/{hour.DayOfYear:D3}/{hour.Hour:D2}/";

    // OR_GLM-L2-LCFA_G19_s20262872142400_e20262872143000_c20262872143017.nc: start and end as
    // year, day of year, hour, minute, second and tenths.
    private static readonly Regex FileName = new(@"_s(\d{14})_e(\d{14})_c\d{14}\.nc$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParseFileTimes(string key, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        var match = FileName.Match(key);
        return match.Success && TryParseStamp(match.Groups[1].Value, out start) && TryParseStamp(match.Groups[2].Value, out end);
    }

    private static bool TryParseStamp(string stamp, out DateTimeOffset at)
    {
        at = default;
        var year = int.Parse(stamp[..4], CultureInfo.InvariantCulture);
        var day = int.Parse(stamp[4..7], CultureInfo.InvariantCulture);
        var hour = int.Parse(stamp[7..9], CultureInfo.InvariantCulture);
        var minute = int.Parse(stamp[9..11], CultureInfo.InvariantCulture);
        var second = int.Parse(stamp[11..13], CultureInfo.InvariantCulture);
        var tenth = int.Parse(stamp[13..14], CultureInfo.InvariantCulture);
        if (day < 1 || day > 366 || hour > 23 || minute > 59 || second > 60) return false;
        at = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddDays(day - 1).AddHours(hour).AddMinutes(minute).AddSeconds(second).AddMilliseconds(tenth * 100);
        return true;
    }

    public static IReadOnlyList<GlmFileEntry> ParseListing(string xml)
    {
        var document = XDocument.Parse(xml);
        var entries = new List<GlmFileEntry>();
        foreach (var contents in document.Descendants().Where(e => e.Name.LocalName == "Contents"))
        {
            var key = contents.Elements().FirstOrDefault(e => e.Name.LocalName == "Key")?.Value;
            var sizeText = contents.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value;
            if (key is null || !long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size <= 0) continue;
            if (!TryParseFileTimes(key, out var start, out var end)) continue;
            entries.Add(new GlmFileEntry(key, size, start, end));
        }
        return entries;
    }

    // The flash table out of one file: centroid, time of the first light, radiant energy, and the
    // quality flag (anything but 0 is a flash the processing itself doubts - glint, a degraded
    // pixel, one cut off at the edge of a file).
    public static IReadOnlyList<LightningFlash> ParseFlashes(Stream stream, DateTimeOffset fileStart)
    {
        var file = H5File.Open(stream);
        var latitudes = file.Dataset("flash_lat").Read<float[]>();
        var longitudes = file.Dataset("flash_lon").Read<float[]>();
        var timeDataset = file.Dataset("flash_time_offset_of_first_event");
        var times = timeDataset.Read<ushort[]>();
        var (timeScale, timeOffset) = Packing(timeDataset);
        var energyDataset = file.Dataset("flash_energy");
        var energies = energyDataset.Read<ushort[]>();
        var (energyScale, energyOffset) = Packing(energyDataset);
        var quality = file.Dataset("flash_quality_flag").Read<short[]>();

        var count = new[] { latitudes.Length, longitudes.Length, times.Length, energies.Length, quality.Length }.Min();
        var flashes = new List<LightningFlash>(count);
        for (var i = 0; i < count; i++)
        {
            if (quality[i] != 0) continue;
            var lat = latitudes[i];
            var lon = longitudes[i];
            if (!float.IsFinite(lat) || !float.IsFinite(lon) || Math.Abs(lat) > 90 || Math.Abs(lon) > 180) continue;
            var seconds = times[i] * timeScale + timeOffset;
            flashes.Add(new LightningFlash(lat, lon, fileStart.AddMilliseconds(Math.Round(seconds * 1000)), (energies[i] * energyScale + energyOffset) * 1e15));
        }
        return flashes;
    }

    private static (double Scale, double Offset) Packing(IH5Dataset dataset)
    {
        static double Read(IH5Dataset d, string name, double fallback)
        {
            if (!d.AttributeExists(name)) return fallback;
            var attribute = d.Attribute(name);
            return attribute.Type.Size == 8 ? attribute.Read<double[]>()[0] : attribute.Read<float[]>()[0];
        }
        return (Read(dataset, "scale_factor", 1), Read(dataset, "add_offset", 0));
    }
}

public readonly record struct GlmFileEntry(string Key, long Size, DateTimeOffset Start, DateTimeOffset End);

public readonly record struct LightningFlash(float Latitude, float Longitude, DateTimeOffset Time, double EnergyFemtojoules);

public sealed record LightningBatch(DateTimeOffset Start, DateTimeOffset End, long Sequence, IReadOnlyList<LightningFlash> Flashes);

public readonly record struct LightningBox(double West, double South, double East, double North)
{
    // The map wraps round the world, so its box can run past ±180.
    public bool Contains(double lat, double lon)
    {
        if (lat < South || lat > North) return false;
        if (East - West >= 360) return true;
        return (lon >= West && lon <= East) || (lon + 360 >= West && lon + 360 <= East) || (lon - 360 >= West && lon - 360 <= East);
    }
}

public sealed class LightningAnswer
{
    public string State { get; init; } = "loading";
    public string Epoch { get; init; } = "";
    public long Sequence { get; init; }
    public bool Incremental { get; init; }
    public DateTimeOffset? CoverageStart { get; init; }
    public DateTimeOffset? CoverageEnd { get; init; }
    public long Origin { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<double[]> Flashes { get; init; } = [];
}

// A read-only, seekable view of a file on a web server that fetches it in fixed blocks with Range
// requests as they are read. HDF5 readers hop around a file a few bytes at a time, so a block is
// fetched once and kept; reading the flash table out of a GLM file touches about a dozen.
public sealed class HttpRangeStream(HttpClient client, string url, long length, int blockSize = 16 * 1024) : Stream
{
    private readonly Dictionary<long, byte[]> _blocks = [];
    private long _position;

    public int Requests { get; private set; }
    public long BytesFetched { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        var total = 0;
        while (destination.Length > 0 && _position < length)
        {
            var index = _position / blockSize;
            var block = Block(index);
            var within = (int)(_position - index * blockSize);
            var take = Math.Min(destination.Length, block.Length - within);
            if (take <= 0) break;
            block.AsSpan(within, take).CopyTo(destination);
            destination = destination[take..];
            _position += take;
            total += take;
        }
        return total;
    }

    private byte[] Block(long index)
    {
        if (_blocks.TryGetValue(index, out var cached)) return cached;
        var from = index * blockSize;
        var to = Math.Min(length, from + blockSize) - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from, to);
        using var response = client.Send(request, HttpCompletionOption.ResponseContentRead);
        response.EnsureSuccessStatusCode();
        using var body = response.Content.ReadAsStream();
        using var copy = new MemoryStream((int)(to - from + 1));
        body.CopyTo(copy);
        var bytes = copy.ToArray();
        // A server that ignores Range sends the whole file back with a 200.
        if (response.StatusCode == System.Net.HttpStatusCode.OK && bytes.LongLength == length)
            bytes = bytes.AsSpan((int)from, (int)(to - from + 1)).ToArray();
        Requests++;
        BytesFetched += bytes.Length;
        _blocks[index] = bytes;
        return bytes;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
