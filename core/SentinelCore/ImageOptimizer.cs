using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
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
    // Quality for the re-encode a crop or an orientation fix forces while optimization is off.
    // High enough to be visually lossless, because the configured JpegQuality is a size/speed
    // setting and size is precisely what has been switched off.
    private const int PassthroughJpegQuality = 95;

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

        // Enabled governs the downscale and the JPEG re-encode below - the speed knobs - and
        // nothing else. Cropping and auto-orient are not optimizations: the crop decides what part of
        // the scene the model is shown at all, and a sideways frame reads worse at any size. Turning
        // shrinking off to give the AI a better picture must not silently drop a region of
        // interest someone set per camera, so those two still apply here.
        if (!options.Enabled)
        {
            var info = Image.Identify(SafeImageDecoding.Options, sourceBytes)
                       ?? throw new InvalidOperationException("Unable to identify image.");

            // With neither to do there is no reason to decode: the camera's own JPEG goes out
            // byte for byte, which is the no-generation-loss path someone disabling this wants.
            if (crop is null && !NeedsOrientationFix(info.Metadata, options))
            {
                return new OptimizedImage(
                    sourceBytes,
                    info.Width,
                    info.Height,
                    info.Width,
                    info.Height);
            }
        }

        await using var input = new MemoryStream(sourceBytes, writable: false);
        using var image = await Image.LoadAsync(SafeImageDecoding.Options, input, cancellationToken);

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

            if (options.Enabled && (image.Width > options.MaxWidth || image.Height > options.MaxHeight))
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
                // Only reaches the re-encode with optimization off when a crop or an orientation
                // fix forced a decode, and then the configured quality is the wrong number to
                // apply - it is the size/speed setting, and size is exactly what is switched off.
                Quality = options.Enabled ? options.JpegQuality : PassthroughJpegQuality
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
        using var image = await Image.LoadAsync<Rgba32>(SafeImageDecoding.Options, input, cancellationToken);

        if (options.AutoOrient)
            image.Mutate(context => context.AutoOrient());

        var region = ToPixelRectangle(crop, image.Width, image.Height);
        if (region.Width > 0 && region.Height > 0)
        {
            var thickness = Math.Clamp(Math.Min(image.Width, image.Height) / 150, 3, 20);
            DrawRectangleBorder(image, region, new Rgba32(255, 32, 32, 255), thickness);
        }

        await using var output = new MemoryStream();
        // Same rule as the inference path: JpegQuality is the size/speed knob, so it only
        // applies while optimization is on.
        await image.SaveAsJpegAsync(
            output,
            new JpegEncoder { Quality = options.Enabled ? options.JpegQuality : PassthroughJpegQuality },
            cancellationToken);
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

    // Whether AutoOrient would actually change anything, so a frame that is already upright
    // doesn't get decoded and re-encoded for nothing when optimization is off.
    private static bool NeedsOrientationFix(ImageMetadata metadata, ImageProcessingOptions options)
    {
        if (!options.AutoOrient)
            return false;

        if (metadata.ExifProfile is not { } exif)
            return false;

        if (!exif.TryGetValue(ExifTag.Orientation, out var orientation))
            return false;

        // 0 is an unset tag and 1 is "already upright"; everything up to 8 is a rotation,
        // a mirror, or both, all of which AutoOrient has real work to do on.
        return orientation.Value > 1;
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