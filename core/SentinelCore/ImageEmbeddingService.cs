using ElBruno.LocalEmbeddings.ImageEmbeddings;
using ElBruno.LocalEmbeddings.ImageEmbeddings.Downloader;
using ElBruno.LocalEmbeddings.ImageEmbeddings.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public interface IImageEmbeddingService
{
    Task<float[]?> EmbedImageAsync(byte[] imageBytes, CancellationToken cancellationToken);

    Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken);
}

// Turns an alert image (or a free-text search query) into a CLIP embedding vector, entirely
// locally via ONNX Runtime - same "expensive part happens once, locally" shape as
// FaceRecognitionService. Unlike FaceAiSharp's bundled models, CLIP's ~577MB of ONNX weights
// aren't shipped in the NuGet package - they're downloaded once from Hugging Face into this app's
// own data directory the first time semantic search is actually enabled and used, not eagerly at
// every startup regardless of whether the feature is on. Registered as a singleton so the ONNX
// Runtime sessions, once loaded, are reused for the process lifetime.
public sealed class ImageEmbeddingService : IImageEmbeddingService, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<BridgeOptions> _bridgeOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ImageEmbeddingService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ClipImageEncoder? _imageEncoder;
    private ClipTextEncoder? _textEncoder;

    public ImageEmbeddingService(
        HttpClient httpClient,
        IOptions<BridgeOptions> bridgeOptions,
        IHostEnvironment environment,
        ILogger<ImageEmbeddingService> logger)
    {
        _httpClient = httpClient;
        _bridgeOptions = bridgeOptions;
        _environment = environment;
        _logger = logger;
    }

    public async Task<float[]?> EmbedImageAsync(byte[] imageBytes, CancellationToken cancellationToken)
    {
        try
        {
            var (imageEncoder, _) = await EnsureInitializedAsync(cancellationToken);
            using var stream = new MemoryStream(imageBytes);
            return imageEncoder.Encode(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Semantic search is opt-in and purely additive - a model/decode failure here
            // shouldn't take down the rest of alert processing, the same philosophy as
            // FaceRecognitionService.
            _logger.LogWarning(ex, "Image embedding failed for an alert image");
            return null;
        }
    }

    public async Task<float[]?> EmbedTextAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            var (_, textEncoder) = await EnsureInitializedAsync(cancellationToken);
            return textEncoder.Encode(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Text embedding failed for a semantic search query");
            return null;
        }
    }

    private async Task<(ClipImageEncoder ImageEncoder, ClipTextEncoder TextEncoder)> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_imageEncoder is not null && _textEncoder is not null)
            return (_imageEncoder, _textEncoder);

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_imageEncoder is not null && _textEncoder is not null)
                return (_imageEncoder, _textEncoder);

            var configured = _bridgeOptions.Value.DataDirectory;
            var root = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
            var modelDirectory = Path.Combine(root, "clip-model");
            Directory.CreateDirectory(modelDirectory);

            _logger.LogInformation("Ensuring local CLIP model is downloaded to {ModelDirectory} (one-time, ~577MB)", modelDirectory);
            var downloader = new HuggingFaceImageModelDownloader(_httpClient, NullLogger<HuggingFaceImageModelDownloader>.Instance);
            await downloader.EnsureModelDownloadedAsync(modelDirectory, cancellationToken);

            var options = new ImageEmbeddingsOptions { ModelDirectory = modelDirectory };
            _imageEncoder = new ClipImageEncoder(Path.Combine(modelDirectory, options.VisionModelFileName));
            _textEncoder = new ClipTextEncoder(
                Path.Combine(modelDirectory, options.TextModelFileName),
                Path.Combine(modelDirectory, options.VocabFileName),
                Path.Combine(modelDirectory, options.MergesFileName));
            _logger.LogInformation("CLIP model loaded and ready for semantic search");

            return (_imageEncoder, _textEncoder);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _imageEncoder?.Dispose();
        _textEncoder?.Dispose();
        _initLock.Dispose();
    }
}
