using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SentinelCore.Web;

// Reads what an audio file says about itself. TagLibSharp first (ID3v1/v2, Vorbis comments, MP4
// atoms, APE and ASF), through a stream so a file on another computer's share is read the same
// way as a local one; ffprobe for the files TagLib cannot open; then the file and folder names
// for anything the tags left out.
public static class MusicTags
{
    // TagLib reads through whatever stream it is given - here IMediaFileSystem's, which is a
    // seekable SMB stream for a network library.
    private sealed class StreamFile(string name, Stream stream) : TagLib.File.IFileAbstraction
    {
        public string Name => name;
        public Stream ReadStream => stream;
        public Stream WriteStream => stream;
        public void CloseStream(Stream stream) { }
    }

    private static TagLib.File OpenTagLib(IMediaFileSystem fs, string path, out Stream stream)
    {
        stream = fs.OpenRead(path);
        try
        {
            return TagLib.File.Create(new StreamFile(System.IO.Path.GetFileName(path), stream), TagLib.ReadStyle.Average);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // Fills in the track's tags and audio details. Never throws for a file it cannot make sense
    // of: the track still gets a title from its name, and TagError says what went wrong.
    public static async Task ReadAsync(MusicTrack track, MediaFiles files, string ffprobePath, ILogger logger, CancellationToken cancellationToken)
    {
        var fs = files.For(track.Path);
        string? tagLibError = null;
        try
        {
            await Task.Run(() => ReadWithTagLib(track, fs), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            tagLibError = ex.Message;
            if (!await ReadWithFfprobeAsync(track, files, ffprobePath, logger, cancellationToken))
            {
                Reset(track);
                track.TagError = $"The tags could not be read: {ex.Message}";
            }
        }
        if (tagLibError is null) track.TagError = null;
        ApplyNameFallbacks(track);
        track.TagsReadAt = DateTimeOffset.UtcNow;
    }

    public static void ReadWithTagLib(MusicTrack track, IMediaFileSystem fs)
    {
        using var file = OpenTagLib(fs, track.Path, out var stream);
        using var _ = stream;
        var tag = file.Tag;

        Reset(track);
        track.Title = Clean(tag.Title) ?? "";
        var performers = tag.Performers.Select(Clean).OfType<string>().ToArray();
        track.ArtistText = performers.Length switch { 0 => "", 1 => performers[0], _ => string.Join("; ", performers) };
        var albumArtists = tag.AlbumArtists.Select(Clean).OfType<string>().ToArray();
        track.AlbumArtistTag = albumArtists.Length == 0 ? null : string.Join(" & ", albumArtists);
        track.Album = Clean(tag.Album) ?? "";
        track.TrackNumber = tag.Track > 0 ? (int)tag.Track : null;
        track.DiscNumber = tag.Disc > 0 ? (int)tag.Disc : null;
        track.Year = tag.Year is > 1000 and < 3000 ? (int)tag.Year : null;
        track.Genres = MusicText.SplitGenres(tag.Genres);
        track.Composer = Clean(tag.FirstComposer);
        track.Bpm = tag.BeatsPerMinute > 0 ? (int)tag.BeatsPerMinute : null;
        track.TrackGain = Finite(tag.ReplayGainTrackGain);
        track.TrackPeak = Finite(tag.ReplayGainTrackPeak);
        track.AlbumGain = Finite(tag.ReplayGainAlbumGain);
        track.AlbumPeak = Finite(tag.ReplayGainAlbumPeak);
        track.HasEmbeddedArt = tag.Pictures.Any(p => p.Data is { Count: > 0 });
        track.HasEmbeddedLyrics = !string.IsNullOrWhiteSpace(tag.Lyrics);
        track.MusicBrainzReleaseId = Clean(tag.MusicBrainzReleaseId);
        track.MusicBrainzReleaseGroupId = Clean(tag.MusicBrainzReleaseGroupId);
        track.CompilationTag =
            (file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag)?.IsCompilation == true
            || (file.GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment)?.IsCompilation == true
            || (file.GetTag(TagLib.TagTypes.Apple) as TagLib.Mpeg4.AppleTag)?.IsCompilation == true;

        var properties = file.Properties;
        if (properties is not null)
        {
            track.DurationSeconds = Math.Round(properties.Duration.TotalSeconds, 3);
            track.Bitrate = properties.AudioBitrate > 0 ? properties.AudioBitrate * 1000L : null;
            track.SampleRate = properties.AudioSampleRate > 0 ? properties.AudioSampleRate : null;
            track.BitDepth = properties.BitsPerSample > 0 ? properties.BitsPerSample : null;
            track.Channels = properties.AudioChannels > 0 ? properties.AudioChannels : null;
        }
        var description = properties?.Codecs.Where(c => c is not null).Select(c => c.Description).FirstOrDefault() ?? "";
        track.Codec = CodecFor(track.Path, description);
        if (track.Bitrate is null && track.DurationSeconds > 0) track.Bitrate = (long)(track.FileSize * 8 / track.DurationSeconds);
        if (track.Codec is "mp3" or "aac" or "vorbis" or "opus" or "wma") track.BitDepth = null;
    }

    // The codec as clients name it in ?codecs= (MUSIC_AND_PODCASTS.md, Streaming).
    public static string CodecFor(string path, string? description)
    {
        var text = description ?? "";
        bool Says(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "mp3",
            ".flac" => "flac",
            ".opus" => "opus",
            ".ogg" or ".oga" => Says("opus") ? "opus" : Says("flac") ? "flac" : "vorbis",
            ".m4a" or ".m4b" or ".mp4" or ".aac" => Says("alac") || Says("lossless") ? "alac" : "aac",
            ".alac" => "alac",
            ".wav" => "wav",
            ".aif" or ".aiff" => "aiff",
            ".wma" => "wma",
            ".ape" => "ape",
            ".wv" => "wavpack",
            ".dsf" or ".dff" => "dsd",
            var other => other.TrimStart('.')
        };
    }

    public static bool IsLossless(string codec) => codec is "flac" or "alac" or "wav" or "aiff" or "ape" or "wavpack" or "dsd";

    // The ffprobe fallback, for files TagLib refuses (an odd container, a damaged header).
    public static async Task<bool> ReadWithFfprobeAsync(MusicTrack track, MediaFiles files, string ffprobePath, ILogger logger, CancellationToken cancellationToken)
    {
        byte[]? output;
        try
        {
            output = await FfmpegProcess.RunCapturingStdoutAsync(ffprobePath,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-select_streams", "a:0", files.FfmpegInput(track.Path)],
                TimeSpan.FromSeconds(60), logger, $"tag probe of {System.IO.Path.GetFileName(track.Path)}", ProcessPriorityClass.BelowNormal, cancellationToken);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        if (output is null) return false;
        try
        {
            using var document = JsonDocument.Parse(output);
            ApplyFfprobe(track, document.RootElement);
            track.TagError = null;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static void ApplyFfprobe(MusicTrack track, JsonElement root)
    {
        Reset(track);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement element)
        {
            if (element.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var property in t.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String) tags.TryAdd(property.Name, property.Value.GetString()!);
        }
        JsonElement? stream = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            foreach (var s in streams.EnumerateArray())
                if (s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio") { stream = s; Collect(s); break; }
        if (root.TryGetProperty("format", out var format)) Collect(format);

        string? Tag(params string[] names)
        {
            foreach (var name in names)
                if (tags.TryGetValue(name, out var value) && Clean(value) is { } clean) return clean;
            return null;
        }
        track.Title = Tag("title") ?? "";
        track.ArtistText = Tag("artist") ?? "";
        track.AlbumArtistTag = Tag("album_artist", "albumartist", "album artist");
        track.Album = Tag("album") ?? "";
        track.TrackNumber = MusicText.ParseNumber(Tag("track", "tracknumber"));
        track.DiscNumber = MusicText.ParseNumber(Tag("disc", "discnumber"));
        track.Year = MusicText.ParseYear(Tag("date", "year", "originaldate"));
        track.Genres = MusicText.SplitGenres([Tag("genre")]);
        track.Composer = Tag("composer");
        track.Bpm = MusicText.ParseNumber(Tag("TBPM", "bpm"));
        track.TrackGain = MusicText.ParseGain(Tag("REPLAYGAIN_TRACK_GAIN"));
        track.TrackPeak = MusicText.ParseGain(Tag("REPLAYGAIN_TRACK_PEAK"));
        track.AlbumGain = MusicText.ParseGain(Tag("REPLAYGAIN_ALBUM_GAIN"));
        track.AlbumPeak = MusicText.ParseGain(Tag("REPLAYGAIN_ALBUM_PEAK"));
        track.HasEmbeddedLyrics = Tag("lyrics", "unsyncedlyrics", "USLT") is not null || tags.Keys.Any(k => k.StartsWith("lyrics-", StringComparison.OrdinalIgnoreCase));
        track.CompilationTag = Tag("compilation", "TCMP", "cpil") is "1" or "true";
        track.MusicBrainzReleaseId = Tag("MUSICBRAINZ_ALBUMID", "MusicBrainz Album Id");
        track.MusicBrainzReleaseGroupId = Tag("MUSICBRAINZ_RELEASEGROUPID", "MusicBrainz Release Group Id");

        string? codecName = null;
        if (stream is { } audio)
        {
            codecName = String(audio, "codec_name");
            track.SampleRate = int.TryParse(String(audio, "sample_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var rate) ? rate : null;
            track.Channels = audio.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var c) ? c : null;
            track.BitDepth = int.TryParse(String(audio, "bits_per_raw_sample"), NumberStyles.None, CultureInfo.InvariantCulture, out var bits) && bits > 0 ? bits : null;
            track.Bitrate = long.TryParse(String(audio, "bit_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var bitrate) ? bitrate : null;
        }
        if (root.TryGetProperty("format", out format))
        {
            if (double.TryParse(String(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                track.DurationSeconds = Math.Round(duration, 3);
            if (track.Bitrate is null && long.TryParse(String(format, "bit_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var formatBitrate))
                track.Bitrate = formatBitrate;
        }
        track.Codec = codecName switch
        {
            "mp3" => "mp3",
            "aac" => "aac",
            "alac" => "alac",
            "flac" => "flac",
            "vorbis" => "vorbis",
            "opus" => "opus",
            "wmav1" or "wmav2" or "wmapro" or "wmalossless" => "wma",
            "ape" => "ape",
            "wavpack" => "wavpack",
            { } pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => CodecFor(track.Path, null) is "aiff" ? "aiff" : "wav",
            { } dsd when dsd.StartsWith("dsd_", StringComparison.Ordinal) => "dsd",
            _ => CodecFor(track.Path, null)
        };
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        } : null;

    private static void Reset(MusicTrack track)
    {
        track.Title = ""; track.ArtistText = ""; track.AlbumArtistTag = null; track.Album = ""; track.TrackNumber = null;
        track.DiscNumber = null; track.Year = null; track.Genres = []; track.Composer = null; track.Bpm = null;
        track.TrackGain = null; track.TrackPeak = null; track.AlbumGain = null; track.AlbumPeak = null;
        track.HasEmbeddedArt = false; track.HasEmbeddedLyrics = false; track.CompilationTag = false;
        track.MusicBrainzReleaseId = null; track.MusicBrainzReleaseGroupId = null;
        track.DurationSeconds = 0; track.Bitrate = null; track.SampleRate = null; track.BitDepth = null; track.Channels = null;
        track.Codec = CodecFor(track.Path, null);
    }

    // What the names say, for whatever the tags left out: "Artist/Album/01 - Song.mp3" (or
    // "Artist/Album/CD1/01 Song.mp3") is track 1, "Song", on Album by Artist.
    public static void ApplyNameFallbacks(MusicTrack track)
    {
        var segments = track.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : System.IO.Path.GetFileName(track.Path);
        var folders = segments.Length > 1 ? segments[..^1].ToList() : [];
        if (folders.Count > 0 && MusicText.IsDiscFolder(folders[^1]))
        {
            track.DiscNumber ??= MusicText.DiscNumberOf(folders[^1]);
            folders.RemoveAt(folders.Count - 1);
        }
        var parsed = MusicText.ParseFileName(System.IO.Path.GetFileNameWithoutExtension(fileName));
        if (string.IsNullOrWhiteSpace(track.Title)) track.Title = parsed.Title;
        track.TrackNumber ??= parsed.Track;
        track.DiscNumber ??= parsed.Disc;
        if (string.IsNullOrWhiteSpace(track.Album)) track.Album = folders.Count > 0 ? folders[^1] : MusicText.UnknownAlbum;
        if (string.IsNullOrWhiteSpace(track.ArtistText))
            track.ArtistText = track.AlbumArtistTag ?? (folders.Count > 1 ? folders[^2] : MusicText.UnknownArtist);
    }

    // The embedded picture to use as cover art: the front cover if the file says which one that
    // is, else the first.
    public static (byte[] Data, string MimeType)? ReadPicture(IMediaFileSystem fs, string path)
    {
        using var file = OpenTagLib(fs, path, out var stream);
        using var _ = stream;
        var pictures = file.Tag.Pictures.Where(p => p.Data is { Count: > 0 }).ToList();
        var picture = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures.FirstOrDefault();
        return picture is null ? null : (picture.Data.Data, picture.MimeType ?? "image/jpeg");
    }

    public static string? ReadLyrics(IMediaFileSystem fs, string path)
    {
        using var file = OpenTagLib(fs, path, out var stream);
        using var _ = stream;
        return Clean(file.Tag.Lyrics);
    }

    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Replace("\0", "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? Math.Round(value, 4) : null;
}

// The rules that need the whole library at once: which artists a credit names, who an album
// belongs to, and which albums are compilations (MUSIC_AND_PODCASTS.md, Scanning rules).
public static class MusicAlbumRules
{
    public const int CompilationArtistCount = 3;

    // Sets Artists, AlbumArtist, Compilation, AlbumId and ArtistId on every track. Returns the
    // tracks whose answer changed, which are the ones worth writing back.
    public static List<MusicTrack> Resolve(IReadOnlyList<MusicTrack> tracks)
    {
        // Album artists with " & " in them are bands the household owns; their name is never
        // split when it turns up as a track's artist ("Simon & Garfunkel").
        var bands = new HashSet<string>(
            tracks.Select(t => t.AlbumArtistTag).OfType<string>().Where(a => a.Contains('&')),
            StringComparer.OrdinalIgnoreCase);

        var before = tracks.ToDictionary(t => t.Id, Signature);
        foreach (var track in tracks)
        {
            track.Artists = MusicText.SplitArtists(track.ArtistText, bands);
            if (track.Artists.Count == 0) track.Artists = [MusicText.UnknownArtist];
        }

        // Tracks with no album artist are grouped by album title and folder. Three or more
        // different artists on one such album (or a compilation flag) make it Various Artists.
        foreach (var group in tracks.Where(t => t.AlbumArtistTag is null).GroupBy(t => (MusicText.Key(t.Album), AlbumFolder(t))))
        {
            var artists = group.Select(t => MusicText.Key(t.PrimaryArtist)).Distinct().Count();
            var compilation = artists >= CompilationArtistCount || group.Any(t => t.CompilationTag);
            foreach (var track in group)
            {
                track.Compilation = compilation;
                track.AlbumArtist = compilation ? MusicText.VariousArtists : track.PrimaryArtist;
            }
        }
        foreach (var track in tracks.Where(t => t.AlbumArtistTag is not null))
        {
            var various = IsVariousArtists(track.AlbumArtistTag!);
            track.Compilation = various || track.CompilationTag;
            track.AlbumArtist = various ? MusicText.VariousArtists : track.AlbumArtistTag!;
        }

        foreach (var track in tracks)
        {
            track.AlbumId = MusicIds.Album(track.AlbumArtist, track.Album);
            track.ArtistId = MusicIds.Artist(track.PrimaryArtist);
        }
        return tracks.Where(t => before[t.Id] != Signature(t)).ToList();
    }

    public static bool IsVariousArtists(string name) =>
        MusicText.Key(name) is "various artists" or "various" or "va" or "v.a." or "v/a";

    // The album's folder, with a "CD1"/"Disc 2" folder counted as the album folder above it.
    public static string AlbumFolder(MusicTrack track)
    {
        var segments = track.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
        if (segments.Count > 0 && MusicText.IsDiscFolder(segments[^1])) segments.RemoveAt(segments.Count - 1);
        return $"{track.LibraryId:N}/{string.Join('/', segments)}";
    }

    private static string Signature(MusicTrack t) =>
        $"{string.Join('\u001f', t.Artists)}|{t.AlbumArtist}|{t.Compilation}|{t.AlbumId}|{t.ArtistId}";
}
