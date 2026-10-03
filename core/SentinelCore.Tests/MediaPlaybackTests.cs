using System.Text.Json;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Direct play or HLS, copy or re-encode: the decisions that make a film play on a given device,
// or show a black screen with no error. Pinned here because the failure is silent on Apple.
public sealed class MediaPlaybackTests
{
    private static readonly HashSet<string> AppleVideo = ["h264", "hevc"];
    private static readonly HashSet<string> AppleAudio = ["aac", "ac3", "eac3", "mp3"];
    private static readonly HashSet<string> BrowserVideo = ["h264"];
    private static readonly HashSet<string> BrowserAudio = ["aac", "mp3"];

    private static MediaItem Item(string container, string video, string audio, string? tag = null, int audioTracks = 1) => new()
    {
        Id = Guid.NewGuid(),
        Path = $"/films/film.{container}",
        Container = container,
        VideoCodec = video,
        VideoTag = tag,
        AudioCodec = audio,
        AudioTracks = [.. Enumerable.Range(0, audioTracks).Select(i => new MediaAudioTrack(i, i == 0 ? audio : "aac", 2, "eng", null, i == 0))],
        DurationSeconds = 6000
    };

    [Fact]
    public void AnMp4OfH264AndAac_PlaysDirectEverywhere()
    {
        Assert.True(MediaPlaybackPlanner.Plan(Item("mp4", "h264", "aac"), BrowserVideo, BrowserAudio, null).Direct);
        Assert.True(MediaPlaybackPlanner.Plan(Item("mp4", "h264", "aac"), AppleVideo, AppleAudio, null).Direct);
    }

    [Fact]
    public void Mp3Audio_IsConvertedToAac_WhenTheFileGoesThroughHls()
    {
        // An XviD/MP3 .avi: Apple plays MP3 in a file but not inside fMP4 HLS segments.
        var avi = MediaPlaybackPlanner.Plan(Item("avi", "mpeg4", "mp3"), AppleVideo, AppleAudio, null);
        Assert.Equal(("h264", "aac"), (avi.VideoMode, avi.AudioMode));
        var mp4 = MediaPlaybackPlanner.Plan(Item("mp4", "h264", "mp3"), AppleVideo, AppleAudio, null);
        Assert.True(mp4.Direct);
    }

    [Fact]
    public void AnMkv_IsRemuxedNotReencoded()
    {
        var plan = MediaPlaybackPlanner.Plan(Item("mkv", "h264", "aac"), BrowserVideo, BrowserAudio, null);
        Assert.False(plan.Direct);
        Assert.Equal("copy", plan.VideoMode);
        Assert.Equal("copy", plan.AudioMode);
    }

    [Fact]
    public void Hevc_IsCopiedForApple_AndTaggedHvc1_ButTranscodedForABrowser()
    {
        var apple = MediaPlaybackPlanner.Plan(Item("mkv", "hevc", "eac3"), AppleVideo, AppleAudio, null);
        Assert.Equal("copy", apple.VideoMode);
        Assert.Equal("copy", apple.AudioMode);
        Assert.True(apple.TagHvc1);

        var browser = MediaPlaybackPlanner.Plan(Item("mkv", "hevc", "eac3"), BrowserVideo, BrowserAudio, null);
        Assert.Equal("h264", browser.VideoMode);
        Assert.Equal("aac", browser.AudioMode);
        Assert.False(browser.TagHvc1);
    }

    [Fact]
    public void AnHevcMp4TaggedHev1_IsNotDirectPlayedOnApple()
    {
        // AVPlayer shows a black picture for hev1 and reports nothing; the remux retags it.
        Assert.False(MediaPlaybackPlanner.Plan(Item("mp4", "hevc", "aac", tag: "hev1"), AppleVideo, AppleAudio, null).Direct);
        Assert.True(MediaPlaybackPlanner.Plan(Item("mp4", "hevc", "aac", tag: "hvc1"), AppleVideo, AppleAudio, null).Direct);
    }

    // Rambo (2008), a torrent MP4: B-frames, but every frame stamped in decode order, so AVPlayer
    // showed them out of order and the picture wiggled back and forth.
    [Fact]
    public void FramesStampedInDecodeOrder_AreNeverDirectPlayed_AndGetTheirStampsRebuilt()
    {
        var item = Item("mp4", "hevc", "aac", tag: "hvc1");
        item.TimestampsInDecodeOrder = true;

        var copied = MediaPlaybackPlanner.Plan(item, AppleVideo, AppleAudio, null, new HashSet<string> { "h264", "hevc" });
        Assert.False(copied.Direct);
        Assert.True(copied.RebuildTimestamps);
        Assert.Equal("copy", copied.VideoMode);
        var copyArgs = MediaPlaybackPlanner.BuildFfmpegArguments(item, copied, 0, "/tmp/session");
        Assert.Equal("dts2pts", copyArgs[copyArgs.IndexOf("-bsf:v") + 1]);
        Assert.Contains("hvc1", copyArgs);

        // ffmpeg 8.0 (the media box) has dts2pts for H.264 only: HEVC is re-encoded instead.
        var encoded = MediaPlaybackPlanner.Plan(item, AppleVideo, AppleAudio, null, new HashSet<string> { "h264" });
        Assert.False(encoded.Direct);
        Assert.Equal("h264", encoded.VideoMode);
        var encodeArgs = MediaPlaybackPlanner.BuildFfmpegArguments(item, encoded, 0, "/tmp/session");
        Assert.Equal("libx264", encodeArgs[encodeArgs.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-bsf:v", encodeArgs);

        Assert.Equal("h264", MediaPlaybackPlanner.Plan(item, AppleVideo, AppleAudio, null).VideoMode);
    }

    [Fact]
    public void CorrectlyStampedFiles_AreLeftAlone()
    {
        var plan = MediaPlaybackPlanner.Plan(Item("mkv", "hevc", "eac3"), AppleVideo, AppleAudio, null, new HashSet<string> { "hevc" });
        Assert.False(plan.RebuildTimestamps);
        Assert.DoesNotContain("-bsf:v", MediaPlaybackPlanner.BuildFfmpegArguments(Item("mkv", "hevc", "eac3"), plan, 0, "/tmp/session"));
        Assert.True(MediaPlaybackPlanner.Plan(Item("mp4", "hevc", "aac", tag: "hvc1"), AppleVideo, AppleAudio, null, new HashSet<string> { "hevc" }).Direct);
    }

    [Fact]
    public void FrameStamps_RunningBackwards_AsTheDecoderShowsThem_MeanDecodeOrderStamping()
    {
        string Csv(IEnumerable<long> pts) => string.Join("\n", pts.Select(p => $"{p},"));
        var inOrder = Enumerable.Range(0, 240).Select(i => i * 50050L);
        // Shown 0 1 2 3 but stamped with their decode slots 0 3 1 2 (I P B B), as Rambo was.
        var scrambled = Enumerable.Range(0, 60).SelectMany(g => new long[] { g * 4, g * 4 + 3, g * 4 + 1, g * 4 + 2 }).Select(n => n * 50050L);
        // E.T. (1982) in a quiet scene: the same fault, but the encoder only used a few B-frames.
        var quietScene = inOrder.Select((pts, i) => i % 60 == 30 ? pts + 120_000 : pts);

        Assert.False(MediaProbe.ShownOutOfOrder(Csv(inOrder)));
        Assert.True(MediaProbe.ShownOutOfOrder(Csv(scrambled)));
        Assert.True(MediaProbe.ShownOutOfOrder(Csv(quietScene)));
        Assert.False(MediaProbe.ShownOutOfOrder(Csv(inOrder.Select((pts, i) => i == 100 ? pts + 120_000 : pts))), "one stray step is not proof");
        Assert.False(MediaProbe.ShownOutOfOrder(Csv(scrambled.Take(20))), "too few frames to tell");
        Assert.False(MediaProbe.ShownOutOfOrder(string.Concat(Enumerable.Repeat("N/A,\n", 240))), "AVI frames carry no stamps");
    }

    [Fact]
    public void BFrames_ComeFromTheFirstRealVideoStream()
    {
        using var withB = JsonDocument.Parse("""{"streams":[{"codec_type":"video","has_b_frames":2},{"codec_type":"audio"}]}""");
        using var withoutB = JsonDocument.Parse("""{"streams":[{"codec_type":"video","has_b_frames":0}]}""");
        Assert.True(MediaProbe.HasBFrames(withB.RootElement));
        Assert.False(MediaProbe.HasBFrames(withoutB.RootElement));
    }

    [Fact]
    public void Dts2ptsCodecs_AreReadFromFfmpegsHelp()
    {
        Assert.Equal(["h264", "hevc"], MediaPlaybackPlanner.ParseBsfCodecs("Bit stream filter dts2pts\n    Supported codecs: h264 hevc\n\nExiting with exit code 0\n").Order());
        Assert.Empty(MediaPlaybackPlanner.ParseBsfCodecs("Unknown bitstream filter 'dts2pts'.\n"));
    }

    [Fact]
    public void ChoosingANonDefaultAudioTrack_GoesThroughHls()
    {
        var plan = MediaPlaybackPlanner.Plan(Item("mp4", "h264", "aac", audioTracks: 2), BrowserVideo, BrowserAudio, 1);
        Assert.False(plan.Direct);
        Assert.Equal(1, plan.AudioTrackIndex);
    }

    [Fact]
    public void FfmpegArguments_SeekBeforeTheInput_AndWriteFragmentedMp4()
    {
        var item = Item("mkv", "hevc", "eac3");
        var plan = MediaPlaybackPlanner.Plan(item, AppleVideo, AppleAudio, null);
        var args = MediaPlaybackPlanner.BuildFfmpegArguments(item, plan, 1234.5, "/tmp/session");
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"), "seeking after -i decodes everything up to the start point");
        Assert.Equal("1234.5", args[args.IndexOf("-ss") + 1]);
        Assert.Contains("fmp4", args);
        Assert.Contains("hvc1", args);
        Assert.Contains("event", args);
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
    }

    [Fact]
    public void Codecs_DefaultWhenTheClientSaysNothing()
    {
        Assert.Equal(["h264"], MediaPlaybackPlanner.ParseCodecs(null, "h264"));
        Assert.Equal(["h264", "hevc"], MediaPlaybackPlanner.ParseCodecs("H264, hevc", "h264").Order());
    }

    [Fact]
    public void EventPlaylists_AreToldToStartAtTheBeginning()
    {
        var playlist = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXTINF:6.0,\nseg_00000.m4s\n";
        var served = MediaEndpoints.WithStartAtBeginning(playlist);
        Assert.StartsWith("#EXTM3U\n#EXT-X-START:TIME-OFFSET=0", served);
        Assert.Equal(served, MediaEndpoints.WithStartAtBeginning(served));
    }

    [Fact]
    public void Subtitles_ShiftToTheSessionStart_AndDropWhatIsAlreadyOver()
    {
        var vtt = "WEBVTT\n\n1\n00:00:05.000 --> 00:00:08.000\nGone\n\n2\n00:01:40.000 --> 00:01:45.500 line:90%\nKept\n";
        var shifted = MediaSubtitles.Shift(vtt, 100);
        Assert.DoesNotContain("Gone", shifted);
        Assert.Contains("00:00:00.000 --> 00:00:05.500 line:90%", shifted);
        Assert.Contains("Kept", shifted);
        Assert.Equal(vtt, MediaSubtitles.Shift(vtt, 0));
    }

    [Fact]
    public void Progress_PercentAndWatched()
    {
        Assert.Equal(50, new MediaProgress("", Guid.Empty, 60, 120, false, DateTimeOffset.UtcNow).Percent);
        Assert.Equal(100, new MediaProgress("", Guid.Empty, 0, 120, true, DateTimeOffset.UtcNow).Percent);
        Assert.Equal(0, new MediaProgress("", Guid.Empty, 60, null, false, DateTimeOffset.UtcNow).Percent);
    }

    // What ffprobe printed for a real episode of the library this was built for (trimmed): a
    // Matroska file with 10-bit HEVC, E-AC-3 5.1 and a SubRip track.
    private const string BlackMirrorProbe = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "profile": "Main 10", "codec_type": "video", "codec_tag_string": "[0][0][0][0]",
              "width": 1920, "height": 1080, "pix_fmt": "yuv420p10le", "disposition": { "default": 1, "attached_pic": 0 },
              "tags": { "DURATION-eng": "00:44:06.040000000" } },
            { "index": 1, "codec_name": "eac3", "codec_type": "audio", "channels": 6, "disposition": { "default": 1 },
              "tags": { "language": "eng", "title": "English 5.1" } },
            { "index": 2, "codec_name": "subrip", "codec_type": "subtitle", "disposition": { "default": 0, "forced": 0 },
              "tags": { "language": "eng" } },
            { "index": 3, "codec_name": "hdmv_pgs_subtitle", "codec_type": "subtitle", "disposition": { "default": 0 } },
            { "index": 4, "codec_name": "mjpeg", "codec_type": "video", "disposition": { "attached_pic": 1 } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "2646.040000", "bit_rate": "4630000" }
        }
        """;

    [Fact]
    public void Probe_ReadsCodecsTracksAndDuration()
    {
        var item = new MediaItem { Path = "/nowhere/Black Mirror - S01E01.mkv" };
        using var document = JsonDocument.Parse(BlackMirrorProbe);
        MediaProbe.Apply(item, document.RootElement);

        Assert.Equal("mkv", item.Container);
        Assert.Equal("hevc", item.VideoCodec);
        Assert.Equal(1920, item.Width);
        Assert.Equal(2646.04, item.DurationSeconds!.Value, 2);
        Assert.Equal(4_630_000, item.Bitrate);
        Assert.Equal("eac3", item.AudioCodec);
        Assert.Equal(6, item.AudioChannels);
        // The PGS track is a picture, not text, and cannot be offered as WebVTT; cover art is not video.
        var subtitle = Assert.Single(item.SubtitleTracks);
        Assert.Equal("subrip", subtitle.Codec);
        Assert.Equal(0, subtitle.StreamIndex);
    }

    [Fact]
    public void Probe_FallsBackToTheMatroskaDurationTag()
    {
        var item = new MediaItem { Path = "/nowhere/x.mkv" };
        using var document = JsonDocument.Parse(BlackMirrorProbe.Replace("\"duration\": \"2646.040000\",", ""));
        MediaProbe.Apply(item, document.RootElement);
        Assert.Equal(2646.04, item.DurationSeconds!.Value, 2);
    }

    // The YIFY layout: "Movie.srt" beside "Movie.mp4", no language in between. This once threw
    // (a negative substring length), which failed playback with a 500 and stopped the scan.
    [Fact]
    public void Sidecars_AreFoundWithAndWithoutALanguage()
    {
        var directory = Directory.CreateTempSubdirectory("sidecars").FullName;
        try
        {
            var video = Path.Combine(directory, "17.Again.2009.1080p.BrRip.x264.YIFY.mp4");
            foreach (var name in new[]
            {
                "17.Again.2009.1080p.BrRip.x264.YIFY.mp4", "17.Again.2009.1080p.BrRip.x264.YIFY.srt",
                "17.Again.2009.1080p.BrRip.x264.YIFY.eng.forced.srt", "17.Again.2009.1080p.BrRip.x264.YIFY.nfo",
                "17.Again.srt", "Other.srt",
            })
                File.WriteAllText(Path.Combine(directory, name), "");

            var found = MediaProbe.FindSidecarSubtitles(LocalMediaFileSystem.Instance, video);

            Assert.Equal(2, found.Count);
            Assert.Contains(found, s => s.External!.EndsWith("YIFY.srt") && s.Language is null && !s.Forced);
            Assert.Contains(found, s => s.External!.EndsWith("YIFY.eng.forced.srt") && s.Language == "eng" && s.Forced);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("/usr/bin/ffmpeg", "/usr/bin/ffprobe")]
    [InlineData("ffmpeg", "ffprobe")]
    [InlineData("/opt/tools/avconv", "ffprobe")]
    public void Ffprobe_IsFoundBesideFfmpeg(string ffmpeg, string ffprobe) => Assert.Equal(ffprobe, MediaProbe.DeriveFfprobePath(ffmpeg));
}
