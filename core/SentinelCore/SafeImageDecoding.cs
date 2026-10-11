using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace SentinelCore;

// The one way this codebase decodes an image it didn't make itself - camera snapshots, uploads to
// Family Files and Organizer, known-face photos, cover art fetched from the internet.
//
// Only the formats those actually arrive in are registered. TIFF (and TGA, PBM, QOI) are left out
// on purpose: ImageSharp 3.x has unfixed TIFF bugs (GHSA-wmxv-xphr-5c9g, a 24-byte BigTIFF that
// spins a decoder thread; GHSA-j3p4-wp97-rph4 and GHSA-j9gm/jjfr for TIFF processing and encoding),
// and the fix only exists in 4.x, which needs a paid build-time license key for Release builds -
// for this project and for anyone building it from source. Without a TIFF decoder none of those
// paths can be reached. The fifth advisory (GHSA-gwg2-r3hj-4w44, ICC CLUT parsing) is only
// reachable on 3.x through IccProfile.Entries, which nothing here reads.
//
// Metadata is still decoded, so EXIF orientation keeps phone photos the right way up.
public static class SafeImageDecoding
{
    public static Configuration Configuration { get; } = new(
        new JpegConfigurationModule(),
        new PngConfigurationModule(),
        new GifConfigurationModule(),
        new WebpConfigurationModule(),
        new BmpConfigurationModule());

    public static DecoderOptions Options { get; } = new() { Configuration = Configuration };
}
