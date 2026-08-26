using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore;

// One detection from the fast local model: a COCO-91 class id, a sigmoid score, and a box in
// center-x/center-y/width/height form, normalized 0-1 relative to the analyzed region (the
// profile's crop if one was set, the full frame otherwise) - the same region the VLM path sees.
public sealed record FastLocalDetection(
    int ClassId,
    double Score,
    double CenterX,
    double CenterY,
    double Width,
    double Height);

// How much of the square model input the analyzed region actually occupies once it has been
// scaled to fit and centered, as fractions of the input's width and height. ScaleX/ScaleY are
// equal whenever the region is scaled uniformly (which ResizeMode.Pad always does), but keeping
// both axes independent means the un-padding math stays correct if the resize mode ever changes
// again - and makes the identity case (a square region, no padding at all) fall out as 1.0.
public sealed record Letterbox(double ScaleX, double ScaleY)
{
    public static Letterbox For(int regionWidth, int regionHeight)
    {
        if (regionWidth <= 0 || regionHeight <= 0) return new Letterbox(1, 1);

        // Uniform scale to fit the square: the longer side fills it, the shorter side is padded.
        return regionWidth >= regionHeight
            ? new Letterbox(1, (double)regionHeight / regionWidth)
            : new Letterbox((double)regionWidth / regionHeight, 1);
    }

    // Model output coordinates are normalized to the padded square. Strip the centered bars and
    // rescale so the result is normalized to the analyzed region again, matching what
    // FastLocalDetection documents. Values can land slightly outside 0-1 when a box overhangs
    // the region's edge, exactly as they could before letterboxing.
    public double UnpadX(double x) => (x - (1 - ScaleX) / 2) / ScaleX;

    public double UnpadY(double y) => (y - (1 - ScaleY) / 2) / ScaleY;
}

public interface IFastLocalDetector
{
    Task<IReadOnlyList<FastLocalDetection>> DetectAsync(byte[] imageBytes, CropRegion? crop, CancellationToken cancellationToken);

    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    // The model file's base name (e.g. "rf-detr-nano"), for reporting which backend/model
    // produced an analysis - "unknown" until the session has actually been loaded once.
    string ModelName { get; }
}

// Runs a small object-detection model (RF-DETR Nano by default) entirely locally via ONNX
// Runtime on the CPU - the engine behind FastLocalVisionClient. Same lifecycle shape as
// ImageEmbeddingService: the model is a one-time ~100MB download into the data directory,
// deferred to first actual use so installs that never route to "fastlocal" don't pay for it,
// and the loaded session is a singleton reused for the process lifetime.
//
// The model contract (verified against rf-detr-nano.onnx): input "input" 1x3xSxS float,
// ImageNet-normalized RGB; outputs "pred_boxes" 1xQx4 (cxcywh, normalized to the input) and
// "pred_logits" 1xQx91 (COCO-91 class ids, sigmoid scores, no NMS needed - DETR queries
// already correspond to distinct objects).
public sealed class FastLocalDetector : IFastLocalDetector, IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<FastLocalOptions> _options;
    private readonly IOptions<BridgeOptions> _bridgeOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<FastLocalDetector> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private string _inputName = "input";
    private int _inputSize;

    public string ModelName { get; private set; } = "unknown";

    public FastLocalDetector(
        HttpClient httpClient,
        IOptionsMonitor<FastLocalOptions> options,
        IOptions<BridgeOptions> bridgeOptions,
        IHostEnvironment environment,
        ILogger<FastLocalDetector> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _bridgeOptions = bridgeOptions;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FastLocalDetection>> DetectAsync(
        byte[] imageBytes, CropRegion? crop, CancellationToken cancellationToken)
    {
        var session = await EnsureInitializedAsync(cancellationToken);
        var options = _options.CurrentValue;

        var startedAt = DateTimeOffset.UtcNow;
        var (tensor, letterbox) = await PreprocessAsync(imageBytes, crop, _inputSize, cancellationToken);

        using var results = session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        var boxes = (DenseTensor<float>)results.First(r => r.Name == "pred_boxes").Value;
        var logits = (DenseTensor<float>)results.First(r => r.Name == "pred_logits").Value;

        var queryCount = logits.Dimensions[1];
        var classCount = logits.Dimensions[2];
        var detections = new List<FastLocalDetection>();

        for (var q = 0; q < queryCount; q++)
        {
            var bestClass = 0;
            var bestLogit = float.MinValue;
            // Class 0 is unused in the COCO-91 numbering (ids start at 1 = person).
            for (var c = 1; c < classCount; c++)
            {
                var logit = logits[0, q, c];
                if (logit > bestLogit)
                {
                    bestLogit = logit;
                    bestClass = c;
                }
            }

            var score = 1.0 / (1.0 + Math.Exp(-bestLogit));
            if (score < options.ScoreThreshold)
                continue;

            detections.Add(new FastLocalDetection(
                bestClass,
                score,
                letterbox.UnpadX(boxes[0, q, 0]),
                letterbox.UnpadY(boxes[0, q, 1]),
                boxes[0, q, 2] / letterbox.ScaleX,
                boxes[0, q, 3] / letterbox.ScaleY));
        }

        _logger.LogInformation(
            "Fast local detector ({Model}) found {Count} object(s) above {Threshold:F2} in {ElapsedMs:F0}ms",
            ModelName,
            detections.Count,
            options.ScoreThreshold,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

        return detections;
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
            return true;

        // Not yet initialized: ready if the model is already on disk, or can be fetched on first
        // use. Deliberately doesn't trigger the download itself - a health probe shouldn't block
        // on (or repeatedly retry) a ~100MB transfer; a failed download surfaces on the first
        // real alert instead, where FailoverVisionClient's fallback can still save the analysis.
        var options = _options.CurrentValue;
        if (File.Exists(ResolveModelPath(options)))
            return true;

        return await Task.FromResult(!string.IsNullOrWhiteSpace(options.DownloadUrl));
    }

    private async Task<InferenceSession> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
            return _session;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
                return _session;

            var options = _options.CurrentValue;
            var modelPath = ResolveModelPath(options);

            if (!File.Exists(modelPath))
                await DownloadModelAsync(options, modelPath, cancellationToken);

            var session = new InferenceSession(modelPath);

            var input = session.InputMetadata.First();
            _inputName = input.Key;
            // Prefer the model's own static input size over the configured one - a mismatch
            // would otherwise fail on every single alert with an opaque shape error.
            var dims = input.Value.Dimensions;
            _inputSize = dims is [_, _, > 0, > 0] ? dims[2] : options.InputSize;

            ModelName = Path.GetFileNameWithoutExtension(modelPath);
            _session = session;
            _logger.LogInformation(
                "Fast local detector loaded {Model} (input {Size}x{Size})", ModelName, _inputSize, _inputSize);
            return session;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task DownloadModelAsync(FastLocalOptions options, string modelPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.DownloadUrl))
            throw new InvalidOperationException(
                $"Fast local model not found at '{modelPath}' and FastLocal:DownloadUrl is not set.");

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        _logger.LogInformation(
            "Downloading fast local model from {Url} to {Path} (one-time, ~100MB)",
            options.DownloadUrl,
            modelPath);

        // Download to a temp name and move into place only after the checksum passes, so a
        // half-written or corrupted file can never be picked up as the model on a later start.
        var tempPath = modelPath + ".download";
        try
        {
            await using (var target = File.Create(tempPath))
            {
                await using var source = await _httpClient.GetStreamAsync(options.DownloadUrl, cancellationToken);
                await source.CopyToAsync(target, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(options.DownloadSha256))
            {
                await using var stream = File.OpenRead(tempPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!hash.Equals(options.DownloadSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Fast local model download failed checksum verification (expected {options.DownloadSha256}, got {hash}).");
            }

            AtomicFile.Replace(tempPath, modelPath);
            _logger.LogInformation("Fast local model downloaded and verified");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private string ResolveModelPath(FastLocalOptions options)
    {
        if (Path.IsPathRooted(options.ModelPath))
            return options.ModelPath;

        var configured = _bridgeOptions.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
        return Path.Combine(root, options.ModelPath);
    }

    private static async Task<(DenseTensor<float> Tensor, Letterbox Letterbox)> PreprocessAsync(
        byte[] imageBytes, CropRegion? crop, int inputSize, CancellationToken cancellationToken)
    {
        await using var input = new MemoryStream(imageBytes, writable: false);
        using var image = await Image.LoadAsync<Rgb24>(input, cancellationToken);

        var analyzedWidth = image.Width;
        var analyzedHeight = image.Height;

        image.Mutate(context =>
        {
            context.AutoOrient();

            if (crop is not null)
            {
                var left = Math.Clamp((int)Math.Round(crop.Left * image.Width), 0, image.Width);
                var top = Math.Clamp((int)Math.Round(crop.Top * image.Height), 0, image.Height);
                var right = Math.Clamp((int)Math.Round(crop.Right * image.Width), left, image.Width);
                var bottom = Math.Clamp((int)Math.Round(crop.Bottom * image.Height), top, image.Height);
                if (right > left && bottom > top)
                {
                    context.Crop(new Rectangle(left, top, right - left, bottom - top));
                    analyzedWidth = right - left;
                    analyzedHeight = bottom - top;
                }
            }

            // Letterbox, not plain stretch. Squashing a wide crop into the square input distorts
            // people far more than the uncropped frame does - the gate profile's full-width,
            // 58%-tall band is 2.3:1, so a standing person arrives 2.3x narrower than they are.
            // Measured on the 2026-08-11 05:42 gate event (a person opening the gate for a truck,
            // which the detector missed entirely): best person score 0.38 stretched vs 0.66
            // letterboxed, against a 0.60 threshold. Letterboxing also *lowered* the empty-frame
            // noise floor (night 0.17 -> 0.15, day 0.42 -> 0.23), so it widens the gap from both
            // sides rather than trading recall for false positives.
            //
            // The cost is that output box coordinates are now normalized to the padded square,
            // not to the analyzed region, so they have to be mapped back - see Letterbox above.
            // Nothing consumes the boxes today (FastLocalVisionClient.MapToAnalysis reads only
            // ClassId and Score), but FastLocalDetection documents them as region-normalized and
            // that contract is cheap to keep honest.
            context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Pad,
                Position = AnchorPositionMode.Center,
                Size = new Size(inputSize, inputSize)
            });
        });

        var tensor = new DenseTensor<float>([1, 3, inputSize, inputSize]);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = (row[x].R / 255f - Mean[0]) / Std[0];
                    tensor[0, 1, y, x] = (row[x].G / 255f - Mean[1]) / Std[1];
                    tensor[0, 2, y, x] = (row[x].B / 255f - Mean[2]) / Std[2];
                }
            }
        });

        return (tensor, Letterbox.For(analyzedWidth, analyzedHeight));
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
