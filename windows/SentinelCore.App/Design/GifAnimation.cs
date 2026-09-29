using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SentinelCore.App.Design;

/// <summary>
/// Plays an animated GIF in a WPF <see cref="Image"/>, which on its own only ever shows the first
/// frame. Used for the NWS radar loop when the interactive radar map can't run.
/// </summary>
/// <remarks>
/// A GIF frame is only a patch: it has its own offset, and a disposal rule for what the next frame
/// is drawn over. Each frame is composed onto the full canvas once, up front, so playing is just
/// swapping finished pictures.
/// </remarks>
public sealed class GifAnimation : IDisposable
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private readonly List<(BitmapSource Frame, TimeSpan Delay)> _frames;
    private readonly DispatcherTimer _timer;
    private Image? _target;
    private int _index;

    private GifAnimation(List<(BitmapSource, TimeSpan)> frames)
    {
        _frames = frames;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Advance();
    }

    public int FrameCount => _frames.Count;

    /// <summary>Decodes and composes every frame. Throws for data that isn't a GIF.</summary>
    public static GifAnimation Decode(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("The picture has no frames.");

        BitmapMetadata? global;
        try
        {
            global = decoder.Metadata;
        }
        catch (Exception)
        {
            global = null;
        }
        int width = Query<ushort>(global, "/logscrdesc/Width") ?? (ushort)decoder.Frames[0].PixelWidth;
        int height = Query<ushort>(global, "/logscrdesc/Height") ?? (ushort)decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0) (width, height) = (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        var full = new Rect(0, 0, width, height);

        var frames = new List<(BitmapSource, TimeSpan)>();
        BitmapSource? baseline = null;
        foreach (var frame in decoder.Frames)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            var left = Query<ushort>(metadata, "/imgdesc/Left") ?? 0;
            var top = Query<ushort>(metadata, "/imgdesc/Top") ?? 0;
            var delayHundredths = Query<ushort>(metadata, "/grctlext/Delay") ?? 0;
            var disposal = Query<byte>(metadata, "/grctlext/Disposal") ?? 0;
            var patch = new Rect(left, top, frame.PixelWidth, frame.PixelHeight);

            var composed = Render(width, height, context =>
            {
                if (baseline is not null) context.DrawImage(baseline, full);
                context.DrawImage(frame, patch);
            });
            frames.Add((composed, delayHundredths > 1 ? TimeSpan.FromMilliseconds(delayHundredths * 10) : DefaultDelay));

            // What the next frame is drawn over.
            baseline = disposal switch
            {
                // Restore to background: this frame's patch is cleared again.
                2 => Render(width, height, context =>
                {
                    context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(patch)));
                    context.DrawImage(composed, full);
                    context.Pop();
                }),
                // Restore to previous: as though this frame had never been drawn.
                3 => baseline,
                _ => composed,
            };
        }
        return new GifAnimation(frames);
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) draw(context);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? Query<T>(BitmapMetadata? metadata, string query) where T : struct
    {
        try
        {
            return metadata?.GetQuery(query) is T value ? value : null;
        }
        catch (Exception)
        {
            // Not every GIF carries every block, and WIC throws rather than answering null.
            return null;
        }
    }

    /// <summary>Shows the frames in <paramref name="image"/>, from the first, and loops.</summary>
    public void Play(Image image)
    {
        _target = image;
        _index = 0;
        image.Source = _frames[0].Frame;
        Resume();
    }

    public void Pause() => _timer.Stop();

    public void Resume()
    {
        if (_target is null || _frames.Count < 2) return;
        _timer.Interval = _frames[_index].Delay;
        _timer.Start();
    }

    private void Advance()
    {
        if (_target is null) return;
        _index = (_index + 1) % _frames.Count;
        _target.Source = _frames[_index].Frame;
        _timer.Interval = _frames[_index].Delay;
    }

    public void Dispose()
    {
        _timer.Stop();
        _target = null;
    }
}
