using System.Text;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Real feeds are not tidy. These fixtures are shaped like ones seen in the wild: a well-kept
// Podcasting 2.0 feed, a hand-rolled one with bare ampersands, HTML entities and broken dates,
// one with no guids, a moved feed, an Atom feed - plus the transcript formats a feed can point at.
public sealed class PodcastParsingTests
{
    private static PodcastFeed Parse(string xml) => PodcastFeedParser.Parse(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void AWellKeptFeed_GivesEveryField()
    {
        var feed = Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:podcast="https://podcastindex.org/namespace/1.0"
                 xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:atom="http://www.w3.org/2005/Atom">
              <channel>
                <title>The Daily Test</title>
                <atom:link href="https://feeds.example.com/daily" rel="self" type="application/rss+xml"/>
                <link>https://example.com/daily</link>
                <language>en-us</language>
                <description><![CDATA[<p>News, <b>daily</b>.</p>]]></description>
                <itunes:author>Example Times</itunes:author>
                <itunes:explicit>yes</itunes:explicit>
                <itunes:image href="https://example.com/daily.jpg"/>
                <itunes:category text="News"><itunes:category text="Daily News"/></itunes:category>
                <podcast:funding url="https://example.com/support">Support the show</podcast:funding>
                <item>
                  <title>Episode 512: The one with everything</title>
                  <guid isPermaLink="false">ep-512</guid>
                  <pubDate>Thu, 02 Oct 2025 09:49:00 +0000</pubDate>
                  <itunes:duration>29:40</itunes:duration>
                  <itunes:season>3</itunes:season>
                  <itunes:episode>512</itunes:episode>
                  <itunes:episodeType>Bonus</itunes:episodeType>
                  <itunes:image href="https://example.com/ep512.jpg"/>
                  <description>Short text.</description>
                  <content:encoded><![CDATA[<p>Long <a href="https://example.com/x" onclick="evil()">text</a> with <script>alert(1)</script>much more detail than the short one.</p>]]></content:encoded>
                  <enclosure url="https://cdn.example.com/512.mp3" length="28000000" type="audio/mpeg"/>
                  <podcast:transcript url="https://example.com/512.vtt" type="text/vtt"/>
                  <podcast:transcript url="https://example.com/512.json" type="application/json" language="en"/>
                  <podcast:chapters url="https://example.com/512.chapters.json" type="application/json+chapters"/>
                </item>
              </channel>
            </rss>
            """);

        Assert.Equal("The Daily Test", feed.Title);
        Assert.Equal("Example Times", feed.Author);
        Assert.Equal("https://example.com/daily", feed.Link);
        Assert.Equal("https://example.com/daily.jpg", feed.ImageUrl);
        Assert.Equal(["News", "Daily News"], feed.Categories);
        Assert.True(feed.Explicit);
        Assert.Equal("en-us", feed.Language);
        var funding = Assert.Single(feed.Funding);
        Assert.Equal("Support the show", funding.Title);

        var item = Assert.Single(feed.Items);
        Assert.Equal("ep-512", item.Key);
        Assert.Equal(1780, item.DurationSeconds);
        Assert.Equal(3, item.Season);
        Assert.Equal(512, item.Episode);
        Assert.Equal("bonus", item.EpisodeType);
        Assert.Equal(new DateTimeOffset(2025, 10, 2, 9, 49, 0, TimeSpan.Zero), item.PublishedAt);
        Assert.Equal("https://example.com/ep512.jpg", item.ImageUrl);
        Assert.Equal(28000000, item.Length);
        Assert.Equal(2, item.Transcripts.Count);
        Assert.Equal("https://example.com/512.chapters.json", item.ChaptersUrl);
        Assert.Contains("much more detail", item.DescriptionHtml);

        var safe = PodcastText.SanitizeHtml(item.DescriptionHtml);
        Assert.Equal("<p>Long <a href=\"https://example.com/x\">text</a> with much more detail than the short one.</p>", safe);
    }

    [Fact]
    public void AHandRolledFeed_WithBareAmpersandsEntitiesAndBadDates_StillReads()
    {
        var feed = Parse("""

              <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
            <channel>
            <title>Tom & Jerry&nbsp;Talk</title>
            <item>
              <title>Cats &amp; Dogs &mdash; part 1</title>
              <guid>one</guid>
              <pubDate>Wednesday, 2 October 2024 08:00 PDT</pubDate>
              <itunes:duration>1:02:03</itunes:duration>
              <enclosure url="https://example.com/1.mp3" type="audio/mpeg"/>
            </item>
            <item>
              <title>Part 2</title>
              <guid>two</guid>
              <pubDate>sometime last week</pubDate>
              <itunes:duration>3723.5</itunes:duration>
              <enclosure url="https://example.com/2.m4a"/>
            </item>
            <item>
              <title>A blog post with no audio</title>
              <guid>three</guid>
            </item>
            </channel>
            </rss>
            """);

        Assert.Equal("Tom & Jerry Talk", feed.Title);
        Assert.Equal(2, feed.Items.Count);
        Assert.Equal("Cats & Dogs — part 1", feed.Items[0].Title);
        Assert.Equal(new DateTimeOffset(2024, 10, 2, 15, 0, 0, TimeSpan.Zero), feed.Items[0].PublishedAt);
        Assert.Equal(3723, feed.Items[0].DurationSeconds);
        Assert.Null(feed.Items[1].PublishedAt);
        Assert.Equal(3723.5, feed.Items[1].DurationSeconds);
        // No type on the enclosure: guessed from the file name.
        Assert.Equal("audio/mp4", feed.Items[1].MimeType);
    }

    [Fact]
    public void AnItemWithNoGuid_IsKnownByItsEnclosure_AndDuplicatesAreDropped()
    {
        var feed = Parse(PodcastFeeds.Rss("No guids", """
            <item><title>A</title><enclosure url="https://example.com/a.mp3" type="audio/mpeg"/></item>
            <item><title>A again</title><enclosure url="https://example.com/a.mp3" type="audio/mpeg"/></item>
            <item><title>B</title><guid>   </guid><enclosure url="https://example.com/b.mp3" type="audio/mpeg"/></item>
            """));
        Assert.Equal(["https://example.com/a.mp3", "https://example.com/b.mp3"], feed.Items.Select(i => i.Key));
    }

    [Fact]
    public void ANewFeedUrl_IsReported()
    {
        var feed = Parse(PodcastFeeds.Rss("Moved", "", "<itunes:new-feed-url>https://new.example.com/feed.xml</itunes:new-feed-url>"));
        Assert.Equal("https://new.example.com/feed.xml", feed.NewFeedUrl);
    }

    [Fact]
    public void AnAtomFeed_Reads()
    {
        var feed = Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Show</title>
              <entry>
                <id>urn:1</id>
                <title>First</title>
                <published>2024-05-01T10:00:00Z</published>
                <link rel="enclosure" href="https://example.com/1.mp3" type="audio/mpeg" length="123"/>
              </entry>
            </feed>
            """);
        var item = Assert.Single(feed.Items);
        Assert.Equal("urn:1", item.Key);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Fact]
    public void AWebPage_IsNotAFeed()
    {
        var error = Assert.Throws<PodcastFeedException>(() => Parse("<html><body>Hello</body></html>"));
        Assert.Contains("web page", error.Message);
    }

    [Theory]
    [InlineData("3723", 3723)]
    [InlineData("62:03", 3723)]
    [InlineData("1:02:03", 3723)]
    [InlineData("01:02:03.500", 3723.5)]
    [InlineData("1h 2m 3s", 3723)]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("45", 45)]
    public void Durations_InEveryFormat(string text, double expected) =>
        Assert.Equal(expected, PodcastFeedParser.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("soon")]
    [InlineData("1:2:3:4")]
    public void Durations_ThatAreNot(string text) => Assert.Null(PodcastFeedParser.ParseDuration(text));

    [Theory]
    [InlineData("Wed, 02 Oct 2002 08:00:00 EST", "2002-10-02T13:00:00Z")]
    [InlineData("Wed, 02 Oct 2002 13:00:00 GMT", "2002-10-02T13:00:00Z")]
    [InlineData("Wed, 2 Oct 2002 08:00:00 -0500", "2002-10-02T13:00:00Z")]
    [InlineData("Mon, 02 Oct 2002 13:00:00 +0000", "2002-10-02T13:00:00Z")]
    [InlineData("02 Oct 2002 13:00 +0000", "2002-10-02T13:00:00Z")]
    [InlineData("2 Sept 2002 13:00:00 GMT", "2002-09-02T13:00:00Z")]
    [InlineData("2002-10-02T15:00:00+02:00", "2002-10-02T13:00:00Z")]
    [InlineData("October 2, 2002", "2002-10-02T00:00:00Z")]
    public void Dates_HandWrittenOrNot(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PodcastFeedParser.ParseDate(text));

    [Fact]
    public void Html_IsCutDownToTheAllowedTags()
    {
        var html = """
            <h2>Show notes</h2><div class="x">Links:<ul><li><a href="javascript:alert(1)">bad</a></li><li><a href='https://good.example'>good</a></li></ul></div>
            <img src="x.png"><style>p{}</style><p>One<p>Two</p><blockquote><i>quoted</i></blockquote><span>&amp; more</span>
            """;
        Assert.Equal(
            "<p>Show notes</p><p>Links:<ul><li>bad</li><li><a href=\"https://good.example\">good</a></li></ul></p>\n<p>One</p><p>Two</p><blockquote><i>quoted</i></blockquote>&amp; more",
            PodcastText.SanitizeHtml(html));
    }

    [Fact]
    public void PlainTextDescriptions_GetParagraphsAndLinks()
    {
        Assert.Equal("<p>Hello.<br>See https://x.example/a.</p><p>Bye &amp; thanks</p>".Replace("https://x.example/a", "<a href=\"https://x.example/a\">https://x.example/a</a>"),
            PodcastText.SanitizeHtml("Hello.\nSee https://x.example/a.\n\nBye & thanks"));
        Assert.Equal("Hello\n\n• one\n• two", PodcastText.ToPlainText("<p>Hello</p><ul><li>one</li><li>two</li></ul>"));
    }

    [Fact]
    public void FeedAddresses_AreNormalized()
    {
        Assert.Equal("https://feeds.example.com/show", PodcastFeedUrl.Normalize("feeds.example.com/show"));
        Assert.Equal("https://feeds.example.com/show", PodcastFeedUrl.Normalize("feed://feeds.example.com/show"));
        Assert.Equal("https://feeds.example.com/show", PodcastFeedUrl.Normalize("itpc://feeds.example.com/show"));
        Assert.Null(PodcastFeedUrl.Normalize("not a url at all"));
        Assert.Equal(PodcastFeedUrl.Key("http://Feeds.Example.com/show/"), PodcastFeedUrl.Key("https://feeds.example.com/show"));
    }

    // ---- transcripts ------------------------------------------------------------------------

    [Fact]
    public void WebVtt_WithVoices()
    {
        var transcript = PodcastTranscriptParser.Parse(Encoding.UTF8.GetBytes("""
            WEBVTT

            NOTE a comment

            1
            00:00:00.000 --> 00:00:04.200
            <v Host>Welcome to the show, everybody listening today.</v>

            00:04.200 --> 00:09.000 align:start
            <v Guest>Thanks for having me on, it is great to be here.
            """), "text/vtt", "feed", "en");
        Assert.NotNull(transcript);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(("Host", 0.0, 4.2), (transcript.Segments[0].Speaker, transcript.Segments[0].Start, transcript.Segments[0].End));
        Assert.Equal("Welcome to the show, everybody listening today.", transcript.Segments[0].Text);
        Assert.Equal("Guest", transcript.Segments[1].Speaker);
        Assert.Equal(4.2, transcript.Segments[1].Start);
    }

    [Fact]
    public void Srt()
    {
        var transcript = PodcastTranscriptParser.Parse(Encoding.UTF8.GetBytes(
            "1\r\n00:00:01,000 --> 00:00:03,500\r\nThe first line of the episode here.\r\n\r\n2\r\n01:00:03,000 --> 01:00:04,000\r\nAnd one an hour later, still talking.\r\n"),
            "application/x-subrip", "feed", null);
        Assert.NotNull(transcript);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(3.5, transcript.Segments[0].End);
        Assert.Equal(3603, transcript.Segments[1].Start);
    }

    [Fact]
    public void PodcastJson_WordByWord_IsJoinedIntoLines()
    {
        var transcript = PodcastTranscriptParser.Parse(Encoding.UTF8.GetBytes("""
            { "version": "1.0.0", "segments": [
              { "speaker": "Ann", "startTime": 0.5, "endTime": 0.9, "body": "Hello" },
              { "speaker": "Ann", "startTime": 0.9, "endTime": 1.4, "body": "there." },
              { "speaker": "Bob", "startTime": "1.6", "endTime": 2.0, "body": "Hi" },
              { "speaker": "Bob", "startTime": 2.0, "endTime": 2.5, "body": "Ann" }
            ] }
            """), "application/json", "feed", "en");
        Assert.NotNull(transcript);
        Assert.Equal(["Hello there.", "Hi Ann"], transcript.Segments.Select(s => s.Text));
        Assert.Equal((0.5, 1.4), (transcript.Segments[0].Start, transcript.Segments[0].End));
        Assert.Equal("Bob", transcript.Segments[1].Speaker);
    }

    [Fact]
    public void PodcastHtml()
    {
        var transcript = PodcastTranscriptParser.Parse(Encoding.UTF8.GetBytes("""
            <cite>Kevin:</cite>
            <time>0:00</time>
            <p>Hello and welcome to this show about all sorts of things.</p>
            <cite>Jen:</cite>
            <time>1:05</time>
            <p>Thanks Kevin &amp; hello everybody listening at home.</p>
            """), "text/html", "feed", null);
        Assert.NotNull(transcript);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(("Kevin", 0.0, 65.0), (transcript.Segments[0].Speaker, transcript.Segments[0].Start, transcript.Segments[0].End));
        Assert.Equal("Thanks Kevin & hello everybody listening at home.", transcript.Segments[1].Text);
    }

    [Fact]
    public void WhisperJson()
    {
        var transcript = PodcastTranscriptParser.ParseWhisperJson("""
            { "result": { "language": "en" },
              "transcription": [ { "timestamps": { "from": "00:00:00,000", "to": "00:00:04,260" }, "offsets": { "from": 0, "to": 4260 }, "text": " Welcome to the show." } ] }
            """);
        Assert.NotNull(transcript);
        Assert.Equal("en", transcript.Language);
        var segment = Assert.Single(transcript.Segments);
        Assert.Equal((0.0, 4.26, "Welcome to the show."), (segment.Start, segment.End, segment.Text));
    }

    [Fact]
    public void BookmarkSnippet_IsTheTranscriptAroundTheMoment()
    {
        var transcript = new PodcastTranscript("feed", "en",
        [
            new(0, 10, null, "far before"),
            new(20, 28, null, "just before"),
            new(29, 35, null, "right at it"),
            new(40, 44, null, "just after"),
            new(50, 60, null, "far after")
        ]);
        Assert.Equal("just before right at it just after", PodcastTranscriptParser.Snippet(transcript, 30 - 15, 30 + 15));
        Assert.Null(PodcastTranscriptParser.Snippet(transcript, 100, 130));
    }

    [Fact]
    public void FtsQuery_QuotesWhatPeopleType()
    {
        Assert.Equal("\"sourdough\" \"starter\"*", SqlitePodcastStore.FtsQuery("sourdough starter"));
        Assert.Equal("\"OR\" \"NEAR\" \"bread\"*", SqlitePodcastStore.FtsQuery("OR NEAR( bread\""));
        Assert.Null(SqlitePodcastStore.FtsQuery("  ** "));
    }

    // ---- trim silence -----------------------------------------------------------------------

    [Fact]
    public void Silences_AreReadFromFfmpeg_AndCutDownToAThirdOfASecond()
    {
        var silences = PodcastAudioProcessor.ParseSilences("""
            [silencedetect @ 0x1] silence_start: 1.999977
            [silencedetect @ 0x1] silence_end: 5.000023 | silence_duration: 3.000045
            [silencedetect @ 0x1] silence_start: 7
            [silencedetect @ 0x1] silence_end: 7.9 | silence_duration: 0.9
            [silencedetect @ 0x1] silence_start: 9.5
            """, duration: 12);
        Assert.Equal(3, silences.Count);
        Assert.Equal((9.5, 12.0), silences[2]);

        var cuts = PodcastAudioProcessor.BuildCuts(silences);
        // The 0.9 s pause is under a second and stays; the others keep 0.15 s at either edge.
        Assert.Equal([new PodcastCut(2.15, 4.85), new PodcastCut(9.65, 11.85)], cuts);
    }

    [Fact]
    public void TheCutMap_ConvertsBothWays()
    {
        var timeline = new PodcastTimeline([new PodcastCut(2.15, 4.85), new PodcastCut(7.15, 8.85)]);
        Assert.Equal(1.0, timeline.ToFile(1.0), 6);
        Assert.Equal(2.15, timeline.ToFile(3.0), 6);           // inside a cut: the join
        Assert.Equal(2.3, timeline.ToFile(5.0), 6);            // after the first cut: 2.7 s earlier
        Assert.Equal(4.45, timeline.ToFile(7.15), 6);
        Assert.Equal(5.6, timeline.ToFile(10.0), 6);           // after both: 4.4 s earlier

        Assert.Equal(1.0, timeline.ToOriginal(1.0), 6);
        Assert.Equal(5.0, timeline.ToOriginal(2.3), 6);
        Assert.Equal(10.0, timeline.ToOriginal(5.6), 6);
        foreach (var original in new[] { 0.5, 5.5, 6.9, 9.0, 9.99 })
            Assert.Equal(original, timeline.ToOriginal(timeline.ToFile(original)), 6);
    }

    [Fact]
    public void TheTrimFilter_KeepsTheStretchesBetweenTheCuts()
    {
        Assert.Null(PodcastAudioProcessor.FilterFor([], voiceBoost: false));
        Assert.Equal($"[0:a:0]anull,{PodcastAudioProcessor.VoiceBoostFilter}[out]", PodcastAudioProcessor.FilterFor([], voiceBoost: true));
        var filter = PodcastAudioProcessor.FilterFor([new PodcastCut(2.15, 4.85), new PodcastCut(7.15, 8.85)], voiceBoost: true)!;
        Assert.StartsWith("[0:a:0]asplit=3[s0][s1][s2];", filter);
        Assert.Contains("[s0]atrim=start=0:end=2.15,asetpts=PTS-STARTPTS[k0];", filter);
        Assert.Contains("[s1]atrim=start=4.85:end=7.15,asetpts=PTS-STARTPTS[k1];", filter);
        Assert.Contains("[s2]atrim=start=8.85,asetpts=PTS-STARTPTS[k2];", filter);
        Assert.EndsWith($"[k0][k1][k2]concat=n=3:v=0:a=1,{PodcastAudioProcessor.VoiceBoostFilter}[out]", filter);
        // A cut right at the start leaves nothing before it.
        Assert.StartsWith("[0:a:0]asplit=1[s0];[s0]atrim=start=3,", PodcastAudioProcessor.FilterFor([new PodcastCut(0, 3)], voiceBoost: false));
    }

    [Fact]
    public void FfprobeChapters_AreRead()
    {
        var probe = PodcastAudioProcessor.ParseProbe("""
            { "chapters": [ { "start_time": "0.000000", "end_time": "5.000000", "tags": { "title": "Opening" } },
                            { "start_time": "5.000000", "end_time": "10.000000" } ],
              "format": { "duration": "10.0" } }
            """);
        Assert.Equal(10, probe.DurationSeconds);
        Assert.Equal(["Opening", "Chapter 2"], probe.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void PodcastChaptersJson_SkipsHiddenOnes_AndFillsInEnds()
    {
        var chapters = PodcastService.ParseChaptersJson(Encoding.UTF8.GetBytes("""
            { "version": "1.2.0", "chapters": [
              { "startTime": 0, "title": "Intro" },
              { "startTime": 30, "title": "Image change", "toc": false },
              { "startTime": 95, "title": "Interview", "img": "https://example.com/i.jpg", "url": "https://example.com" }
            ] }
            """), duration: 600);
        Assert.Equal(["Intro", "Interview"], chapters.Select(c => c.Title));
        Assert.Equal(95, chapters[0].EndSeconds);
        Assert.Equal(600, chapters[1].EndSeconds);
        Assert.Equal("https://example.com/i.jpg", chapters[1].ImageUrl);
    }

    [Fact]
    public void PlayedRules()
    {
        Assert.True(PodcastProgress.IsPlayed(1751, 1780));     // within 30 s of the end
        Assert.True(PodcastProgress.IsPlayed(95, 100));        // 95 %
        Assert.False(PodcastProgress.IsPlayed(1600, 1780));
        Assert.False(PodcastProgress.IsPlayed(60, 100));
        Assert.False(PodcastProgress.IsPlayed(500, null));
    }

    // ---- OPML -------------------------------------------------------------------------------

    [Fact]
    public void Opml_RoundTrips_AndNestedFoldersAreRead()
    {
        var shows = new[]
        {
            new PodcastShow { Title = "One & Only", FeedUrl = "https://example.com/one.xml", Link = "https://example.com/one" },
            new PodcastShow { Title = "Two", FeedUrl = "https://example.com/two.xml" }
        };
        var opml = PodcastEndpoints.Opml(shows);
        Assert.Equal([("https://example.com/one.xml", "One & Only"), ("https://example.com/two.xml", "Two")], PodcastEndpoints.ParseOpml(opml));

        var nested = PodcastEndpoints.ParseOpml("""
            <?xml version="1.0"?>
            <opml version="1.0"><head><title>Export</title></head><body>
              <outline text="feeds"><outline type="rss" text="Three" xmlUrl="https://example.com/three.xml"/></outline>
              <outline text="Four" xmlurl="https://example.com/four.xml"/>
            </body></opml>
            """);
        Assert.Equal(["https://example.com/three.xml", "https://example.com/four.xml"], nested.Select(o => o.Url));
        Assert.Throws<System.Xml.XmlException>(() => PodcastEndpoints.ParseOpml("<rss/>"));
    }
}
