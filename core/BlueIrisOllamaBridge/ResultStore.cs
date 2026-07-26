using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IResultStore
{
    Task SaveAsync(AlertResult result, CancellationToken cancellationToken);
    Task<AlertResult?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<string?> SaveImageAsync(IncomingAlert alert, CancellationToken cancellationToken);
}

public sealed class FileResultStore : IResultStore
{
    // The file is the durable source of truth; this only needs to survive long enough
    // to serve the status polls that follow shortly after an alert is queued/completed.
    private static readonly TimeSpan MemoryCacheDuration = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _resultsDirectory;
    private readonly string _imagesDirectory;
    private readonly IOptionsMonitor<BridgeOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly IEventStore _eventStore;

    public FileResultStore(IOptionsMonitor<BridgeOptions> options, IHostEnvironment environment, IMemoryCache cache, IEventStore eventStore)
    {
        var configured = options.CurrentValue.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _resultsDirectory = Path.Combine(root, "results");
        _imagesDirectory = Path.Combine(root, "images");
        _options = options;
        _cache = cache;
        _eventStore = eventStore;
        Directory.CreateDirectory(_resultsDirectory);
        Directory.CreateDirectory(_imagesDirectory);
    }

    public async Task SaveAsync(AlertResult result, CancellationToken cancellationToken)
    {
        _cache.Set(result.Id, result, MemoryCacheDuration);
        var finalPath = Path.Combine(_resultsDirectory, $"{result.Id:N}.json");
        var temporaryPath = finalPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
        File.Move(temporaryPath, finalPath, true);
        await _eventStore.RecordAsync(result, cancellationToken);
    }

    public async Task<AlertResult?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(id, out AlertResult? cached))
            return cached;

        var path = Path.Combine(_resultsDirectory, $"{id:N}.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<AlertResult>(stream, JsonOptions, cancellationToken);
        if (result is not null) _cache.Set(id, result, MemoryCacheDuration);
        return result;
    }

    public async Task<string?> SaveImageAsync(IncomingAlert alert, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.SaveImages) return null;
        var extension = alert.ContentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        var camera = string.Concat(alert.Camera.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var dayDirectory = Path.Combine(_imagesDirectory, alert.ReceivedAt.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDirectory);
        var path = Path.Combine(dayDirectory, $"{alert.ReceivedAt:HHmmssfff}_{camera}_{alert.Id:N}{extension}");
        await File.WriteAllBytesAsync(path, alert.ImageBytes, cancellationToken);
        return path;
    }
}
