using System.Collections.Concurrent;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _resultsDirectory;
    private readonly string _imagesDirectory;
    private readonly bool _saveImages;
    private readonly ConcurrentDictionary<Guid, AlertResult> _memory = new();

    public FileResultStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        _resultsDirectory = Path.Combine(root, "results");
        _imagesDirectory = Path.Combine(root, "images");
        _saveImages = options.Value.SaveImages;
        Directory.CreateDirectory(_resultsDirectory);
        Directory.CreateDirectory(_imagesDirectory);
    }

    public async Task SaveAsync(AlertResult result, CancellationToken cancellationToken)
    {
        _memory[result.Id] = result;
        var finalPath = Path.Combine(_resultsDirectory, $"{result.Id:N}.json");
        var temporaryPath = finalPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
        File.Move(temporaryPath, finalPath, true);
    }

    public async Task<AlertResult?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(id, out var cached))
            return cached;

        var path = Path.Combine(_resultsDirectory, $"{id:N}.json");
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<AlertResult>(stream, JsonOptions, cancellationToken);
        if (result is not null) _memory[id] = result;
        return result;
    }

    public async Task<string?> SaveImageAsync(IncomingAlert alert, CancellationToken cancellationToken)
    {
        if (!_saveImages) return null;
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
