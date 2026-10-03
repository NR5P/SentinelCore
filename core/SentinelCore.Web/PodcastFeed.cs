using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SentinelCore.Web;

// Reading podcast feeds: RSS 2.0 with Apple's itunes: tags and the Podcasting 2.0 podcast: tags,
// plus the odd Atom feed. Real feeds are messy - dates in a dozen shapes, durations as "1:02:03"
// or "3723" or "62:03", bare ampersands, HTML entities XML has never heard of, items with no
// guid - so everything here takes what it can get and never throws on one bad field.

public sealed class PodcastFeed
{
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? DescriptionHtml { get; set; }
    public string? ImageUrl { get; set; }
    public string? Link { get; set; }
    public List<string> Categories { get; set; } = [];
    public string? Language { get; set; }
    public bool Explicit { get; set; }
    public List<PodcastFunding> Funding { get; set; } = [];
    public string? NewFeedUrl { get; set; }
    public List<PodcastFeedItem> Items { get; set; } = [];
}

public sealed class PodcastFeedItem
{
    public string? Guid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string DescriptionHtml { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string EpisodeType { get; set; } = PodcastEpisodeTypes.Full;
    public bool Explicit { get; set; }
    public string? Link { get; set; }
    public string? ImageUrl { get; set; }
    public string EnclosureUrl { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public long? Length { get; set; }
    public List<PodcastTranscriptLink> Transcripts { get; set; } = [];
    public string? ChaptersUrl { get; set; }

    // The episode's identity in this feed: its guid, or failing that its enclosure URL.
    public string Key => string.IsNullOrWhiteSpace(Guid) ? EnclosureUrl : Guid.Trim();
}

public sealed class PodcastFeedException(string message) : Exception(message);

public static partial class PodcastFeedParser
{
    // Older feeds still declare windows-1252 or iso-8859-x, which .NET only reads with the code
    // page provider registered.
    static PodcastFeedParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static PodcastFeed Parse(byte[] content)
    {
        var document = Load(content);
        var root = document.Root ?? throw new PodcastFeedException("The feed is empty.");
        if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase)) return ParseAtom(root);

        var channel = root.Name.LocalName.Equals("channel", StringComparison.OrdinalIgnoreCase)
            ? root
            : root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel");
        if (channel is null)
            throw new PodcastFeedException(root.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase)
                ? "That address is a web page, not a podcast feed. Look for an \"RSS\" link on the show's site."
                : "That address did not return a podcast feed (no RSS channel in it).");

        var feed = new PodcastFeed
        {
            Title = Clean(Text(channel, "title") ?? Itunes(channel, "title")) ?? "Untitled podcast",
            Author = Clean(Itunes(channel, "author") ?? Text(channel, "managingEditor") ?? Text(channel, "author")
                ?? channel.Elements().FirstOrDefault(e => IsItunes(e) && e.Name.LocalName == "owner")?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value),
            DescriptionHtml = Longest(Text(channel, "description"), Itunes(channel, "summary")),
            ImageUrl = Url(ItunesImage(channel) ?? channel.Elements().FirstOrDefault(e => e.Name.LocalName == "image" && e.Name.Namespace == XNamespace.None)
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "url")?.Value),
            Link = Url(PlainLink(channel)),
            Language = Clean(Text(channel, "language")),
            Explicit = IsExplicit(Itunes(channel, "explicit")),
            NewFeedUrl = Url(Itunes(channel, "new-feed-url"))
        };

        foreach (var category in channel.Elements().Where(e => IsItunes(e) && e.Name.LocalName == "category"))
        {
            AddCategory(feed.Categories, (string?)category.Attribute("text"));
            foreach (var sub in category.Elements().Where(e => IsItunes(e) && e.Name.LocalName == "category"))
                AddCategory(feed.Categories, (string?)sub.Attribute("text"));
        }
        if (feed.Categories.Count == 0)
            foreach (var category in channel.Elements().Where(e => e.Name.LocalName == "category" && e.Name.Namespace == XNamespace.None))
                AddCategory(feed.Categories, category.Value);

        foreach (var funding in channel.Elements().Where(e => IsPodcastNs(e) && e.Name.LocalName == "funding"))
            if (Url((string?)funding.Attribute("url")) is { } url)
                feed.Funding.Add(new PodcastFunding(url, Clean(funding.Value)));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in channel.Elements().Where(e => e.Name.LocalName == "item"))
        {
            var item = ParseItem(element);
            if (item is null || !seen.Add(item.Key)) continue;
            feed.Items.Add(item);
        }
        return feed;
    }

    private static PodcastFeedItem? ParseItem(XElement element)
    {
        var enclosure = element.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure" && Url((string?)e.Attribute("url")) is not null);
        string? enclosureUrl;
        string? type;
        long? length;
        if (enclosure is not null)
        {
            enclosureUrl = Url((string?)enclosure.Attribute("url"));
            type = Clean((string?)enclosure.Attribute("type"));
            length = long.TryParse(((string?)enclosure.Attribute("length"))?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : null;
        }
        else
        {
            // Some feeds only carry Media RSS.
            var media = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "content" && e.Name.NamespaceName.Contains("search.yahoo.com/mrss", StringComparison.Ordinal)
                && Url((string?)e.Attribute("url")) is not null && ((string?)e.Attribute("type") ?? "audio").StartsWith("audio", StringComparison.OrdinalIgnoreCase));
            if (media is null) return null;
            enclosureUrl = Url((string?)media.Attribute("url"));
            type = Clean((string?)media.Attribute("type"));
            length = long.TryParse((string?)media.Attribute("fileSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : null;
        }
        if (enclosureUrl is null) return null;

        var item = new PodcastFeedItem
        {
            Guid = Clean(Text(element, "guid")),
            Title = Clean(Text(element, "title") ?? Itunes(element, "title")) ?? "Untitled episode",
            DescriptionHtml = Longest(
                element.Elements().FirstOrDefault(e => e.Name.LocalName == "encoded" && e.Name.NamespaceName.Contains("purl.org/rss/1.0/modules/content", StringComparison.Ordinal))?.Value,
                Text(element, "description"),
                Itunes(element, "summary")) ?? string.Empty,
            PublishedAt = ParseDate(Text(element, "pubDate") ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "date")?.Value),
            DurationSeconds = ParseDuration(Itunes(element, "duration")),
            Season = ParseNumber(Itunes(element, "season") ?? PodcastNs(element, "season")),
            Episode = ParseNumber(Itunes(element, "episode") ?? PodcastNs(element, "episode")),
            EpisodeType = PodcastEpisodeTypes.Normalize(Itunes(element, "episodeType")),
            Explicit = IsExplicit(Itunes(element, "explicit")),
            Link = Url(PlainLink(element)),
            ImageUrl = Url(ItunesImage(element)),
            EnclosureUrl = enclosureUrl,
            MimeType = string.IsNullOrWhiteSpace(type) ? GuessMimeType(enclosureUrl) : type,
            Length = length
        };

        foreach (var transcript in element.Elements().Where(e => IsPodcastNs(e) && e.Name.LocalName == "transcript"))
            if (Url((string?)transcript.Attribute("url")) is { } url)
                item.Transcripts.Add(new PodcastTranscriptLink(url, Clean((string?)transcript.Attribute("type")) ?? "", Clean((string?)transcript.Attribute("language"))));

        var chapters = element.Elements().FirstOrDefault(e => IsPodcastNs(e) && e.Name.LocalName == "chapters");
        item.ChaptersUrl = Url((string?)chapters?.Attribute("url"));
        return item;
    }

    private static PodcastFeed ParseAtom(XElement feedElement)
    {
        var feed = new PodcastFeed
        {
            Title = Clean(Text(feedElement, "title")) ?? "Untitled podcast",
            Author = Clean(feedElement.Elements().FirstOrDefault(e => e.Name.LocalName == "author")?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value),
            DescriptionHtml = Text(feedElement, "subtitle"),
            ImageUrl = Url(ItunesImage(feedElement) ?? Text(feedElement, "logo") ?? Text(feedElement, "icon")),
            Link = Url(AtomLink(feedElement, "alternate"))
        };
        foreach (var entry in feedElement.Elements().Where(e => e.Name.LocalName == "entry"))
        {
            var enclosure = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && (string?)e.Attribute("rel") == "enclosure");
            var url = Url((string?)enclosure?.Attribute("href"));
            if (url is null) continue;
            feed.Items.Add(new PodcastFeedItem
            {
                Guid = Clean(Text(entry, "id")),
                Title = Clean(Text(entry, "title")) ?? "Untitled episode",
                DescriptionHtml = Longest(Text(entry, "content"), Text(entry, "summary")) ?? string.Empty,
                PublishedAt = ParseDate(Text(entry, "published") ?? Text(entry, "updated")),
                DurationSeconds = ParseDuration(Itunes(entry, "duration")),
                Link = Url(AtomLink(entry, "alternate")),
                EnclosureUrl = url,
                MimeType = Clean((string?)enclosure!.Attribute("type")) ?? GuessMimeType(url),
                Length = long.TryParse((string?)enclosure.Attribute("length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes > 0 ? bytes : null
            });
        }
        return feed;
    }

    // ---- XML that is not quite XML ----------------------------------------------------------

    private static XDocument Load(byte[] content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
            IgnoreComments = true,
            MaxCharactersFromEntities = 1024
        };
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            // The usual suspects: junk before the declaration, bare "&", HTML entities like
            // &nbsp; that XML does not define, and control characters. Fixed up and tried once more.
            var text = Repair(Decode(content));
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), settings);
                return XDocument.Load(reader);
            }
            catch (XmlException ex)
            {
                throw new PodcastFeedException(text.TrimStart().StartsWith('<')
                    ? $"The feed is not valid XML ({ex.Message})"
                    : "That address did not return a podcast feed.");
            }
        }
    }

    private static string Decode(byte[] content)
    {
        // The declaration's encoding wins, as an XML reader would have honoured it.
        var head = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 200));
        var declared = DeclaredEncoding().Match(head);
        if (declared.Success)
        {
            try { return Encoding.GetEncoding(declared.Groups[1].Value).GetString(content); }
            catch (ArgumentException) { }
        }
        return new UTF8Encoding(false, false).GetString(content);
    }

    internal static string Repair(string text)
    {
        var start = text.IndexOf('<');
        if (start > 0) text = text[start..];
        // Drop the declaration: its encoding no longer applies to an already-decoded string.
        text = Declaration().Replace(text, "", 1);
        text = BareAmpersand().Replace(text, "&amp;");
        text = NamedEntity().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (name is "amp" or "lt" or "gt" or "quot" or "apos") return match.Value;
            var decoded = WebUtility.HtmlDecode(match.Value);
            return decoded == match.Value ? "&amp;" + name + ";" : $"&#{char.ConvertToUtf32(decoded, 0)};";
        });
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is '\t' or '\n' or '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) builder.Append(c);
        return builder.ToString();
    }

    [GeneratedRegex(@"encoding\s*=\s*[""']([A-Za-z0-9._-]+)[""']")]
    private static partial Regex DeclaredEncoding();

    [GeneratedRegex(@"^\s*<\?xml[^>]*\?>")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"&(?!(?:[A-Za-z][A-Za-z0-9]{1,31}|#[0-9]{1,7}|#x[0-9A-Fa-f]{1,6});)")]
    private static partial Regex BareAmpersand();

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex NamedEntity();

    // ---- elements ---------------------------------------------------------------------------

    private static bool IsItunes(XElement element) => element.Name.NamespaceName.Contains("itunes.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsPodcastNs(XElement element) =>
        element.Name.NamespaceName.Contains("podcastindex", StringComparison.OrdinalIgnoreCase)
        || element.Name.NamespaceName.Contains("podcast-namespace", StringComparison.OrdinalIgnoreCase);

    private static string? Text(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName && (e.Name.Namespace == XNamespace.None || e.Name.NamespaceName.Contains("w3.org/2005/Atom", StringComparison.Ordinal)))?.Value;

    private static string? Itunes(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName && IsItunes(e))?.Value;

    private static string? PodcastNs(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName && IsPodcastNs(e))?.Value;

    private static string? ItunesImage(XElement parent)
    {
        var image = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "image" && IsItunes(e));
        return (string?)image?.Attribute("href") ?? image?.Value;
    }

    // RSS <link> is plain text; an atom:link sitting beside it is the feed's own address.
    private static string? PlainLink(XElement parent) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && e.Name.Namespace == XNamespace.None && !string.IsNullOrWhiteSpace(e.Value))?.Value;

    private static string? AtomLink(XElement parent, string rel) =>
        (string?)parent.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && ((string?)e.Attribute("rel") ?? "alternate") == rel)?.Attribute("href");

    private static void AddCategory(List<string> categories, string? name)
    {
        var clean = Clean(WebUtility.HtmlDecode(name ?? ""));
        if (clean is not null && !categories.Contains(clean, StringComparer.OrdinalIgnoreCase)) categories.Add(clean);
    }

    private static string? Longest(params string?[] candidates) =>
        candidates.Where(c => !string.IsNullOrWhiteSpace(c)).OrderByDescending(c => c!.Length).FirstOrDefault()?.Trim();

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Whitespace().Replace(text, " ").Trim();
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static bool IsExplicit(string? value) =>
        value?.Trim().ToLowerInvariant() is "yes" or "true" or "explicit";

    private static string? Url(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    public static string GuessMimeType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".m4a" or ".mp4" or ".m4b" => "audio/mp4",
            ".aac" => "audio/aac",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            _ => "audio/mpeg"
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ---- numbers, durations and dates -------------------------------------------------------

    private static int? ParseNumber(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        // podcast:episode may be a decimal (1.5 for a mid-season special); the whole part is shown.
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and < 1_000_000
            ? (int)value
            : null;
    }

    // "3723", "3723.5", "62:03", "1:02:03", "01:02:03.250", "1h 2m 3s", "PT1H2M3S".
    public static double? ParseDuration(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds > 0 ? seconds : null;

        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length > 3) return null;
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return null;
                total = total * 60 + number;
            }
            return total > 0 ? total : null;
        }

        if (value.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
        {
            try { return System.Xml.XmlConvert.ToTimeSpan(value).TotalSeconds is var iso and > 0 ? iso : null; }
            catch (FormatException) { return null; }
        }

        var words = HumanDuration().Match(value);
        if (words.Success)
        {
            double Unit(string group) => words.Groups[group].Success ? double.Parse(words.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
            var total = Unit("h") * 3600 + Unit("m") * 60 + Unit("s");
            return total > 0 ? total : null;
        }
        return null;
    }

    [GeneratedRegex(@"^(?:(?<h>\d+(?:\.\d+)?)\s*h(?:ours?|rs?)?)?\s*(?:(?<m>\d+(?:\.\d+)?)\s*m(?:in(?:utes?|s)?)?)?\s*(?:(?<s>\d+(?:\.\d+)?)\s*s(?:ec(?:onds?|s)?)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HumanDuration();

    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+0000", ["UTC"] = "+0000", ["GMT"] = "+0000", ["Z"] = "+0000",
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
        ["AKST"] = "-0900", ["AKDT"] = "-0800", ["HST"] = "-1000",
        ["BST"] = "+0100", ["IST"] = "+0530", ["CET"] = "+0100", ["CEST"] = "+0200",
        ["EET"] = "+0200", ["EEST"] = "+0300", ["WET"] = "+0000", ["WEST"] = "+0100",
        ["AEST"] = "+1000", ["AEDT"] = "+1100", ["ACST"] = "+0930", ["AWST"] = "+0800",
        ["NZST"] = "+1200", ["NZDT"] = "+1300", ["JST"] = "+0900", ["KST"] = "+0900"
    };

    private static readonly string[] DateFormats =
    [
        "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yyyy H:mm:ss zzz", "d MMM yyyy HH:mm:ss",
        "d MMMM yyyy HH:mm:ss zzz", "d MMMM yyyy HH:mm zzz", "d MMMM yyyy HH:mm:ss", "d MMM yyyy", "d MMMM yyyy",
        "MMM d yyyy HH:mm:ss zzz", "MMM d, yyyy HH:mm:ss zzz", "MMMM d, yyyy", "MMM d, yyyy", "MMM d yyyy",
        "d MMM yy HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
        "d/M/yyyy", "M/d/yyyy"
    ];

    // RFC 822 as written by hand: "Wed, 2 Oct 2002 08:00:00 EST", "Wednesday, 02 October 2002
    // 08:00 PDT", a weekday that does not match the date, "+0000" or "GMT" or nothing at all -
    // plus ISO 8601. Unreadable dates come back null and the episode is ordered by when it was
    // first seen instead.
    public static DateTimeOffset? ParseDate(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return null;

        if (IsoDate().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var iso))
            return iso.ToUniversalTime();

        var normalized = Weekday().Replace(value, "");
        normalized = normalized.Replace(",", " ").Replace("  ", " ");
        normalized = Whitespace().Replace(normalized, " ").Trim();
        // "Sept" is common and .NET only knows "Sep".
        normalized = Regex.Replace(normalized, @"\bSept\b", "Sep", RegexOptions.IgnoreCase);
        normalized = ZoneName().Replace(normalized, match =>
            ZoneOffsets.TryGetValue(match.Groups[1].Value, out var offset) ? offset : match.Value);
        // "+0000" -> "+00:00", which zzz expects.
        normalized = NumericZone().Replace(normalized, "$1$2:$3");

        if (DateTimeOffset.TryParseExact(normalized, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var exact))
            return exact.ToUniversalTime();
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var loose))
            return loose.ToUniversalTime();
        return null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^\s*(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*\.?\s*,?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"(?<=\d\s?)\b([A-Za-z]{1,4})$")]
    private static partial Regex ZoneName();

    [GeneratedRegex(@"([+-])(\d{2}):?(\d{2})$")]
    private static partial Regex NumericZone();
}

// Turning feed HTML into the little HTML the apps render, and into plain text.
public static partial class PodcastText
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    { "p", "br", "a", "b", "i", "em", "strong", "ul", "ol", "li", "blockquote" };

    private static readonly HashSet<string> ParagraphLike = new(StringComparer.Ordinal)
    { "div", "section", "article", "h1", "h2", "h3", "h4", "h5", "h6", "header", "footer", "figure", "pre", "table", "tr" };

    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.Ordinal)
    { "script", "style", "iframe", "object", "embed", "noscript", "template", "svg", "head", "title" };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br" };

    public static bool LooksLikeHtml(string text) => HtmlTag().IsMatch(text);

    // Only p, br, a (http, https and mailto links), b, i, em, strong, ul, ol, li and blockquote
    // survive, without attributes apart from a link's href. Headings and divs become paragraphs,
    // scripts and styles go with their content, and plain text with line breaks is given
    // paragraphs and clickable links.
    public static string SanitizeHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        if (!LooksLikeHtml(input)) return PlainToHtml(WebUtility.HtmlDecode(input));

        var output = new StringBuilder(input.Length);
        var open = new List<string>();
        var dropping = (string?)null;
        var position = 0;
        foreach (Match match in Tag().Matches(input))
        {
            if (dropping is null) AppendText(output, input[position..match.Index]);
            position = match.Index + match.Length;

            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value.ToLowerInvariant();
            if (dropping is not null)
            {
                if (closing && name == dropping) dropping = null;
                continue;
            }
            if (name.StartsWith("!--", StringComparison.Ordinal)) continue;
            if (DroppedWithContent.Contains(name))
            {
                if (!closing && !match.Value.EndsWith("/>", StringComparison.Ordinal)) dropping = name;
                continue;
            }

            if (ParagraphLike.Contains(name)) name = "p";
            if (!AllowedTags.Contains(name)) continue;

            if (VoidTags.Contains(name))
            {
                if (!closing) output.Append("<br>");
                continue;
            }

            if (closing)
            {
                var index = open.LastIndexOf(name);
                if (index < 0) continue;
                for (var i = open.Count - 1; i >= index; i--) output.Append("</").Append(open[i]).Append('>');
                open.RemoveRange(index, open.Count - index);
                continue;
            }

            // A paragraph inside a paragraph is a new paragraph, as browsers read it.
            if (name == "p" && open.LastOrDefault() == "p")
            {
                output.Append("</p>");
                open.RemoveAt(open.Count - 1);
            }

            if (name == "a")
            {
                var href = Href().Match(match.Groups[3].Value);
                var url = href.Success ? WebUtility.HtmlDecode(href.Groups[2].Value.Trim()) : null;
                if (url is null || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                    continue;
                output.Append("<a href=\"").Append(WebUtility.HtmlEncode(url)).Append("\">");
            }
            else
            {
                output.Append('<').Append(name).Append('>');
            }
            open.Add(name);
        }
        if (dropping is null) AppendText(output, input[position..]);
        for (var i = open.Count - 1; i >= 0; i--) output.Append("</").Append(open[i]).Append('>');

        var html = EmptyParagraph().Replace(output.ToString(), "");
        return html.Trim();
    }

    private static void AppendText(StringBuilder output, string raw)
    {
        if (raw.Length == 0) return;
        // A stray "<" that was not a tag, or text the feed double-escaped, ends up as text.
        output.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(raw)));
    }

    private static string PlainToHtml(string text)
    {
        var paragraphs = BlankLines().Split(text.Replace("\r\n", "\n").Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => "<p>" + string.Join("<br>", p.Trim().Split('\n').Select(line => Linkify(WebUtility.HtmlEncode(line.Trim())))) + "</p>");
        return string.Join("", paragraphs);
    }

    private static string Linkify(string encoded) =>
        BareUrl().Replace(encoded, match =>
        {
            var url = match.Value.TrimEnd('.', ',', ')', ';');
            var rest = match.Value[url.Length..];
            return $"<a href=\"{url}\">{url}</a>{rest}";
        });

    // Plain text for summaries, search and notifications: blocks become line breaks, entities are
    // decoded, runs of blank lines collapse to one.
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = DroppedBlock().Replace(html, " ");
        text = LineBreakTag().Replace(text, "\n");
        text = BlockEnd().Replace(text, "\n\n");
        text = ListItem().Replace(text, "\n• ");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ').Replace("\r", "");
        text = SpacesAndTabs().Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim()));
        text = ThreeNewlines().Replace(text, "\n\n");
        return text.Trim();
    }

    public static string Summary(string plain, int max)
    {
        var flat = Whitespace().Replace(plain, " ").Trim();
        if (flat.Length <= max) return flat;
        var cut = flat.LastIndexOf(' ', max - 1);
        if (cut < max / 2) cut = max - 1;
        return flat[..cut].TrimEnd(',', ';', ':', '-', ' ') + "…";
    }

    [GeneratedRegex(@"<\s*/?\s*[A-Za-z][A-Za-z0-9]*[^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"<\s*(/?)\s*([A-Za-z][A-Za-z0-9]*|!--)([^>]*)>", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"href\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Href();

    [GeneratedRegex(@"<p>\s*</p>")]
    private static partial Regex EmptyParagraph();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"https?://[^\s<>""]+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"<(script|style|iframe|noscript|template|svg|head)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DroppedBlock();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</\s*(p|div|h[1-6]|blockquote|ul|ol|section|article|tr|table)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex SpacesAndTabs();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ThreeNewlines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public static class PodcastFeedUrl
{
    // What a person pastes: "feeds.example.com/show", "feed://...", "itpc://...", "podcast://...".
    // Null when it is not a web address at all.
    public static string? Normalize(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var scheme in new[] { "feed://", "itpc://", "pcast://", "podcast://" })
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) text = "https://" + text[scheme.Length..];
        if (text.StartsWith("feed:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        return uri.AbsoluteUri;
    }

    // For telling two spellings of the same feed apart: scheme, case of the host and a trailing
    // slash do not make a different show.
    public static string Key(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return (uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port) + path + uri.Query).ToLowerInvariant();
    }
}

public sealed record PodcastFetchResult(
    bool NotModified,
    byte[]? Content,
    string FinalUrl,
    // Set when every hop to the content was a permanent redirect (301/308): the feed moved.
    string? MovedTo,
    string? ETag,
    string? LastModified);

// Fetches feeds (and the small files they point at: chapters, transcripts) with conditional
// requests, gzip/brotli, and redirects followed here rather than by the handler, so a permanent
// move is noticed and the stored address updated.
public sealed class PodcastHttp(IHttpClientFactory clients)
{
    public const string HttpClientName = "Podcasts";
    public const long MaxFeedBytes = 30 * 1024 * 1024;
    public const long MaxSmallFileBytes = 8 * 1024 * 1024;

    public HttpClient Client() => clients.CreateClient(HttpClientName);

    public async Task<PodcastFetchResult> FetchFeedAsync(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        var current = url;
        var permanent = true;
        var redirected = false;
        for (var hop = 0; hop < 8; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.5");
            if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (!string.IsNullOrEmpty(lastModified)) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

            using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                permanent &= status is 301 or 308;
                redirected = true;
                current = new Uri(new Uri(current), location).AbsoluteUri;
                continue;
            }

            var moved = redirected && permanent ? current : null;
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new PodcastFetchResult(true, null, current, moved, etag, lastModified);
            if (!response.IsSuccessStatusCode)
                throw new PodcastFeedException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.Gone => $"The feed is not at that address any more ({status}).",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"The feed refused the request ({status}). Private feeds need their full address, including any key in it.",
                    _ => $"The feed's server answered {status} {response.ReasonPhrase}."
                });

            var content = await ReadLimitedAsync(response.Content, MaxFeedBytes, cancellationToken)
                ?? throw new PodcastFeedException("The feed is larger than 30 MB, which no real podcast feed is.");
            return new PodcastFetchResult(false, content, current, moved,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture));
        }
        throw new PodcastFeedException("The feed's address redirects too many times.");
    }

    // A chapters or transcript file. Null on any failure: these are extras.
    public async Task<byte[]?> GetSmallAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var current = url;
            for (var hop = 0; hop < 8; hop++)
            {
                using var response = await Client().GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    current = new Uri(new Uri(current), location).AbsoluteUri;
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                return await ReadLimitedAsync(response.Content, MaxSmallFileBytes, timeout.Token);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
