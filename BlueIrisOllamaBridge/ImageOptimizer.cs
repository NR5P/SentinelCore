using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BlueIrisOllamaBridge;

public interface IImageOptimizer
{
    Task<OptimizedImage> OptimizeForInferenceAsync(byte[] sourceBytes, CropRegion? crop, CancellationToken cancellationToken);
}

public sealed record OptimizedImage(byte[] Bytes, int OriginalWidth, int OriginalHeight, int Width, int Height);

public sealed class ImageOptimizer : IImageOptimizer
{
    private readonly ImageProcessingOptions _options;
    private readonly ILogger<ImageOptimizer> _logger;

    public ImageOptimizer(IOptions<ImageProcessingOptions> options, ILogger<ImageOptimizer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OptimizedImage> OptimizeForInferenceAsync(byte[] sourceBytes, CropRegion? crop, CancellationToken cancellationToken)
    {
        // If optimization is disabled, return the original image unchanged.
        if (!_options.Enabled)
        {
            var info = Image.Identify(sourceBytes)
                       ?? throw new InvalidOperationException("Unable to identify image.");

            return new OptimizedImage(
                sourceBytes,
                info.Width,
                info.Height,
                info.Width,
                info.Height);
        }

        await using var input = new MemoryStream(sourceBytes, writable: false);
        using var image = await Image.LoadAsync(input, cancellationToken);

        var originalWidth = image.Width;
        var originalHeight = image.Height;

        image.Mutate(context =>
        {
            if (_options.AutoOrient)
                context.AutoOrient();

            if (crop is not null)
            {
                var region = ToPixelRectangle(crop, image.Width, image.Height);
                if (region.Width > 0 && region.Height > 0)
                    context.Crop(region);
            }

            if (image.Width > _options.MaxWidth || image.Height > _options.MaxHeight)
            {
                context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(_options.MaxWidth, _options.MaxHeight)
                });
            }
        });

        await using var output = new MemoryStream();

        await image.SaveAsJpegAsync(
            output,
            new JpegEncoder
            {
                Quality = _options.JpegQuality
            },
            cancellationToken);

        var optimizedBytes = output.ToArray();

        _logger.LogInformation(
            "Prepared inference image: {OriginalWidth}x{OriginalHeight} ({OriginalBytes:N0} bytes) -> {Width}x{Height} ({OptimizedBytes:N0} bytes)",
            originalWidth,
            originalHeight,
            sourceBytes.Length,
            image.Width,
            image.Height,
            optimizedBytes.Length);

        return new OptimizedImage(
            optimizedBytes,
            originalWidth,
            originalHeight,
            image.Width,
            image.Height);
    }

    private static Rectangle ToPixelRectangle(CropRegion crop, int width, int height)
    {
        var left = Math.Clamp((int)Math.Round(crop.Left * width), 0, width);
        var top = Math.Clamp((int)Math.Round(crop.Top * height), 0, height);
        var right = Math.Clamp((int)Math.Round(crop.Right * width), left, width);
        var bottom = Math.Clamp((int)Math.Round(crop.Bottom * height), top, height);

        return new Rectangle(left, top, right - left, bottom - top);
    }
}