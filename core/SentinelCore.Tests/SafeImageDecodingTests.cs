using System.Diagnostics;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SentinelCore.Tests;

// ImageSharp 3.x's unfixed TIFF advisories are kept unreachable by never registering a TIFF
// decoder (see SafeImageDecoding and the NuGetAuditSuppress note in Directory.Build.props).
public sealed class SafeImageDecodingTests
{
    // A classic little-endian TIFF header, and a BigTIFF one whose first IFD declares an enormous
    // entry count - the shape of GHSA-wmxv-xphr-5c9g, which spins a default-configured decoder.
    private static readonly byte[] Tiff = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] BigTiff =
    [
        0x49, 0x49, 0x2B, 0x00, 0x08, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F
    ];

    [Fact]
    public void Tiff_and_BigTiff_are_refused_straight_away()
    {
        var clock = Stopwatch.StartNew();
        Assert.Throws<UnknownImageFormatException>(() => Image.Load(SafeImageDecoding.Options, Tiff));
        Assert.Throws<UnknownImageFormatException>(() => Image.Load(SafeImageDecoding.Options, BigTiff));
        Assert.Throws<UnknownImageFormatException>(() => Image.DetectFormat(SafeImageDecoding.Options, BigTiff));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"refusing took {clock.Elapsed}");
    }

    [Fact]
    public void The_formats_images_actually_arrive_in_still_decode()
    {
        using var source = new Image<Rgb24>(8, 6, new Rgb24(200, 30, 30));
        foreach (var encoder in new SixLabors.ImageSharp.Formats.IImageEncoder[]
                 { new JpegEncoder(), new PngEncoder(), new GifEncoder(), new WebpEncoder(), new BmpEncoder() })
        {
            using var buffer = new MemoryStream();
            source.Save(buffer, encoder);
            using var decoded = Image.Load(SafeImageDecoding.Options, buffer.ToArray());
            Assert.Equal(8, decoded.Width);
        }
    }

    // Metadata is still decoded, so a phone photo's EXIF orientation still turns it upright.
    [Fact]
    public void Exif_orientation_survives_decoding()
    {
        using var source = new Image<Rgb24>(8, 6);
        source.Metadata.ExifProfile = new ExifProfile();
        source.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        using var buffer = new MemoryStream();
        source.Save(buffer, new JpegEncoder());

        using var decoded = Image.Load(SafeImageDecoding.Options, buffer.ToArray());

        Assert.True(decoded.Metadata.ExifProfile!.TryGetValue(ExifTag.Orientation, out var orientation));
        Assert.Equal((ushort)6, orientation.Value);
    }

    // The guard on all of the above: a new Image.Load somewhere that forgets the options would
    // quietly bring TIFF decoding - and the advisories - back.
    [Fact]
    public void Every_decode_in_the_apps_goes_through_SafeImageDecoding()
    {
        var root = FindCoreDirectory();
        Assert.SkipWhen(root is null, "The sources aren't alongside this build.");

        var decode = new Regex(@"Image\.(Load|LoadAsync|Identify|IdentifyAsync|DetectFormat)(<\w+>)?\((?!SafeImageDecoding\.Options)");
        var offenders = new[] { "SentinelCore", "SentinelCore.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root!, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(path => File.ReadLines(path).Select((line, i) => (path, line, i)))
            .Where(x => decode.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root!, x.path)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Decode without SafeImageDecoding.Options:\n" + string.Join("\n", offenders));
    }

    private static string? FindCoreDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var core = Path.Combine(directory.FullName, "core");
            if (Directory.Exists(Path.Combine(core, "SentinelCore"))) return core;
            if (directory.Name == "core" && Directory.Exists(Path.Combine(directory.FullName, "SentinelCore"))) return directory.FullName;
        }
        return null;
    }
}
