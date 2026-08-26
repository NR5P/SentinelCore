using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SentinelCore.Client.Networking;

namespace SentinelCore.App.Design;

/// <summary>
/// An image fetched from the server over the app's own authenticated session.
/// </summary>
/// <remarks>
/// WPF's BitmapImage can load a URI by itself, but it opens its own connection with no cookies -
/// so against a server that requires a login every snapshot in the feed would come back 401 and the
/// list would be a column of blank rectangles. This routes the fetch through the same
/// <see cref="SentinelClient"/> the rest of the app uses, which is where the session lives.
/// </remarks>
public sealed class RemoteImage : Image
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(RemoteImage),
        new PropertyMetadata(null, OnSourcePathChanged));

    /// <summary>A server-relative path such as "/api/events/{id}/image".</summary>
    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <summary>
    /// Widest edge to decode to. A feed of 4K snapshots decoded at full size is hundreds of
    /// megabytes of bitmap for thumbnails a few hundred pixels wide.
    /// </summary>
    public int DecodeWidth { get; set; } = 640;

    private static void OnSourcePathChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is RemoteImage image) _ = image.LoadAsync(args.NewValue as string);
    }

    private async Task LoadAsync(string? path)
    {
        Source = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        var decodeWidth = DecodeWidth;
        var bitmap = await ImageLoader.LoadAsync(path, decodeWidth).ConfigureAwait(true);

        // The path can change while a fetch is in flight - a recycled list row scrolling fast is
        // the usual way - and the answer to the previous one must not land in the new row.
        if (SourcePath == path) Source = bitmap;
    }
}

/// <summary>
/// Fetches and caches the snapshots the feed, the timeline and the live tiles all show.
/// </summary>
public static class ImageLoader
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new();
    private static readonly ConcurrentQueue<string> Order = new();
    private const int MaxCached = 120;

    private static SentinelClient? _client;

    /// <summary>Hands the loader the session every fetch should ride. Called once at startup.</summary>
    public static void Attach(SentinelClient client) => _client = client;

    /// <summary>Drops everything cached - on reconnect, where the same path may mean a new server.</summary>
    public static void Clear()
    {
        Cache.Clear();
        while (Order.TryDequeue(out _))
        {
        }
    }

    public static async Task<BitmapSource?> LoadAsync(string path, int decodeWidth)
    {
        var key = $"{decodeWidth}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        if (_client is not { } client) return null;
        if (client.Url(path) is not { } url) return null;

        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                CookieContainer = client.Cookies,
                UseCookies = true,
            })
            {
                Timeout = TimeSpan.FromSeconds(20),
            };

            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bitmap = Decode(bytes, decodeWidth);
            if (bitmap is null) return null;

            Cache[key] = bitmap;
            Order.Enqueue(key);
            while (Order.Count > MaxCached && Order.TryDequeue(out var oldest))
            {
                Cache.TryRemove(oldest, out _);
            }
            return bitmap;
        }
        catch (Exception)
        {
            // A snapshot that couldn't be fetched is a blank tile, never an error dialog - the row
            // it belongs to still says what happened and when.
            return null;
        }
    }

    private static BitmapSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            // Frozen so it can be handed to the UI thread from whichever thread decoded it, and so
            // WPF can share one decode between every row showing the same snapshot.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
