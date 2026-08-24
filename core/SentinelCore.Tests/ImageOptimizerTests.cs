using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

// The optimizer sits between every snapshot and every vision model, so what it does to a frame
// decides what the AI is even looking at. These pin the two things that aren't obvious from the
// settings screen: that the width/height limits can never distort a frame whatever they're set
// to, and that switching optimization off doesn't quietly drop a camera's crop region with it.
public sealed class ImageOptimizerTests
{
    [Theory]
    // A square box shrinks a wide frame to fit, aspect intact.
    [InlineData(1600, 900, 768, 768, 768, 432)]
    // A deliberately lopsided box: the short side binds, and the result is still in proportion
    // rather than stretched out to fill the 4096 of allowed width.
    [InlineData(1600, 900, 4096, 256, 455, 256)]
    [InlineData(900, 1600, 256, 4096, 256, 455)]
    // Already inside the box: left alone, never upscaled to fill it.
    [InlineData(320, 240, 4096, 4096, 320, 240)]
    public async Task OptimizeForInference_PreservesAspectRatio_ForAnyLimits(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var optimizer = Build(new ImageProcessingOptions { MaxWidth = maxWidth, MaxHeight = maxHeight });

        var result = await optimizer.OptimizeForInferenceAsync(
            JpegBytes(sourceWidth, sourceHeight),
            crop: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);

        var written = Image.Identify(result.Bytes);
        Assert.Equal(expectedWidth, written.Width);
        Assert.Equal(expectedHeight, written.Height);
    }

    // The crop is a per-profile decision about what the model should look at; Enabled is a
    // speed knob. Turning the speed knob off must not throw away the crop.
    [Fact]
    public async Task OptimizeForInference_AppliesCrop_EvenWhenOptimizationIsDisabled()
    {
        var optimizer = Build(new ImageProcessingOptions { Enabled = false });
        var crop = new CropRegion { Left = 0, Top = 0.30, Right = 1, Bottom = 0.88 };

        var result = await optimizer.OptimizeForInferenceAsync(
            JpegBytes(1600, 1000),
            crop,
            TestContext.Current.CancellationToken);

        Assert.Equal(1600, result.Width);
        Assert.Equal(580, result.Height);
    }

    // ...and with the speed knob off, the crop is the only reason to re-encode, so it shouldn't
    // also drag the frame down to the configured JpegQuality.
    [Fact]
    public async Task OptimizeForInference_IgnoresJpegQuality_WhenOptimizationIsDisabled()
    {
        var source = JpegBytes(1600, 1000);
        var crop = new CropRegion { Left = 0, Top = 0, Right = 1, Bottom = 0.5 };

        var disabled = await Build(new ImageProcessingOptions { Enabled = false, JpegQuality = 5 })
            .OptimizeForInferenceAsync(source, crop, TestContext.Current.CancellationToken);
        var enabled = await Build(new ImageProcessingOptions { Enabled = true, MaxWidth = 4096, MaxHeight = 4096, JpegQuality = 5 })
            .OptimizeForInferenceAsync(source, crop, TestContext.Current.CancellationToken);

        Assert.Equal(disabled.Height, enabled.Height);
        Assert.True(
            disabled.Bytes.Length > enabled.Bytes.Length,
            $"expected the disabled path to ignore JpegQuality 5, got {disabled.Bytes.Length} vs {enabled.Bytes.Length} bytes");
    }

    // Nothing to crop and nothing to straighten: the camera's own JPEG goes out untouched, so
    // disabling optimization still costs no generation loss.
    [Fact]
    public async Task OptimizeForInference_ReturnsSourceBytesUnchanged_WhenDisabledWithNoWorkToDo()
    {
        var source = JpegBytes(1600, 1000);

        var result = await Build(new ImageProcessingOptions { Enabled = false })
            .OptimizeForInferenceAsync(source, crop: null, TestContext.Current.CancellationToken);

        Assert.Same(source, result.Bytes);
        Assert.Equal(1600, result.Width);
        Assert.Equal(1000, result.Height);
    }

    // A rotation tag is real work even with optimization off - a sideways frame reads worse at
    // any size - so this one does decode, and comes back with the sides swapped.
    [Fact]
    public async Task OptimizeForInference_StraightensRotatedFrames_EvenWhenOptimizationIsDisabled()
    {
        // 6 = rotate 90 degrees clockwise.
        var source = JpegBytes(1600, 1000, exifOrientation: 6);

        var straightened = await Build(new ImageProcessingOptions { Enabled = false })
            .OptimizeForInferenceAsync(source, crop: null, TestContext.Current.CancellationToken);
        var left = await Build(new ImageProcessingOptions { Enabled = false, AutoOrient = false })
            .OptimizeForInferenceAsync(source, crop: null, TestContext.Current.CancellationToken);

        Assert.Equal(1000, straightened.Width);
        Assert.Equal(1600, straightened.Height);
        Assert.Same(source, left.Bytes);
    }

    private static ImageOptimizer Build(ImageProcessingOptions options) =>
        new(new StaticOptionsMonitor<ImageProcessingOptions>(options), NullLogger<ImageOptimizer>.Instance);

    private static byte[] JpegBytes(int width, int height, ushort? exifOrientation = null)
    {
        using var image = new Image<Rgb24>(width, height);

        // Deliberately busy, deterministic detail. A flat fill compresses to almost nothing at
        // any quality, which would make the JpegQuality test above pass on a coin flip.
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var noise = (x * 37 + y * 91) % 251;
                    row[x] = new Rgb24((byte)noise, (byte)((noise * 3) % 251), (byte)((x ^ y) % 251));
                }
            }
        });

        if (exifOrientation is { } orientation)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        }

        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder { Quality = 95 });
        return stream.ToArray();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
