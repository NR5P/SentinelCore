using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SentinelCore;

public interface IImageOptimizer
{
    Task<OptimizedImage> OptimizeForInferenceAsync(byte[] sourceBytes, CropRegion? crop, CancellationToken cancellationToken);

    // Draws a border around the crop region on a copy of the source image, for showing
    // a human what area was actually sent to the model. Returns JPEG bytes.
    Task<byte[]> DrawCropOverlayAsync(byte[] sourceBytes, CropRegion crop, CancellationToken cancellationToken);
}

public sealed record OptimizedImage(byte[] Bytes, int OriginalWidth, int OriginalHeight, int Width, int Height);

public sealed class ImageOptimizer : IImageOptimizer
{
    private readonly IOptionsMonitor<ImageProcessingOptions> _options;
    private readonly ILogger<ImageOptimizer> _logger;

    public ImageOptimizer(IOptionsMonitor<ImageProcessingOptions> options, ILogger<ImageOptimizer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<OptimizedImage> OptimizeForInferenceAsync(byte[] sourceBytes, CropRegion? crop, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        // If optimization is disabled, return the original image unchanged.
        if (!options.Enabled)
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
            if (options.AutoOrient)
                context.AutoOrient();

            if (crop is not null)
            {
                var region = ToPixelRectangle(crop, image.Width, image.Height);
                if (region.Width > 0 && region.Height > 0)
                    context.Crop(region);
            }

            if (image.Width > options.MaxWidth || image.Height > options.MaxHeight)
            {
                context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(options.MaxWidth, options.MaxHeight)
                });
            }
        });

        await using var output = new MemoryStream();

        await image.SaveAsJpegAsync(
            output,
            new JpegEncoder
            {
                Quality = options.JpegQuality
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

    public async Task<byte[]> DrawCropOverlayAsync(byte[] sourceBytes, CropRegion crop, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        await using var input = new MemoryStream(sourceBytes, writable: false);
        using var image = await Image.LoadAsync<Rgba32>(input, cancellationToken);

        if (options.AutoOrient)
            image.Mutate(context => context.AutoOrient());

        var region = ToPixelRectangle(crop, image.Width, image.Height);
        if (region.Width > 0 && region.Height > 0)
        {
            var thickness = Math.Clamp(Math.Min(image.Width, image.Height) / 150, 3, 20);
            DrawRectangleBorder(image, region, new Rgba32(255, 32, 32, 255), thickness);
        }

        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = options.JpegQuality }, cancellationToken);
        return output.ToArray();
    }

    private static void DrawRectangleBorder(Image<Rgba32> image, Rectangle region, Rgba32 color, int thickness)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                if (y < region.Top || y >= region.Bottom)
                    continue;

                var onHorizontalEdge = y < region.Top + thickness || y >= region.Bottom - thickness;
                var row = accessor.GetRowSpan(y);

                var left = Math.Max(0, region.Left);
                var right = Math.Min(accessor.Width, region.Right);
                for (var x = left; x < right; x++)
                {
                    var onVerticalEdge = x < region.Left + thickness || x >= region.Right - thickness;
                    if (onHorizontalEdge || onVerticalEdge)
                        row[x] = color;
                }
            }
        });
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