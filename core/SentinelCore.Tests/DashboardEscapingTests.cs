using System.Text.RegularExpressions;
using Xunit;

namespace SentinelCore.Tests;

// The dashboard builds most of its markup with template literals and innerHTML, and escapeHtml is
// the single thing standing between an event summary and the DOM. There are nine copies of it,
// one per script, because these files are plain <script>s with no shared module - so "the escaping
// is right" is nine separate facts, and it only took one of them being wrong.
//
// It was written as the textContent/innerHTML round-trip, which escapes exactly what a text node
// needs: & < >. Quotes are not among them, because a quote means nothing in text. But half the
// interpolations are into attributes - alt=, title=, data-tip=, value= - and there a surviving
// double quote closes the attribute and everything after it parses as more attributes. Verified in
// a real DOM at the time: an event summary reading
//     A man held a sign reading " onerror=alert(1) x="
// produced an <img> carrying an onerror that the markup never contained.
//
// The summaries come from a vision model describing a camera frame, and models quote things - a
// sign, a brand, a number plate - so the merely-broken version of this needed no attacker at all.
//
// Checked from here rather than with a JS test runner because the dashboard has no test setup of
// its own, and a guard that runs in the existing suite is worth more than a correct one that
// nobody runs.
public sealed class DashboardEscapingTests
{
    private static readonly Regex EscapeHtmlBody = new(
        @"function escapeHtml\(value\)\s*\{(?<body>.*?)\n\s*\}", RegexOptions.Singleline);

    public static TheoryData<string> DashboardScripts()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(WwwRoot(), "*.js"))
        {
            if (EscapeHtmlBody.IsMatch(File.ReadAllText(file))) data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DashboardScripts))]
    public void EveryCopyOfEscapeHtml_EscapesQuotesAndNotJustTextNodeCharacters(string script)
    {
        var body = EscapeHtmlBody.Match(File.ReadAllText(Path.Combine(WwwRoot(), script))).Groups["body"].Value;

        // Comments out first: the replacement carries an explanation that names the old approach,
        // and matching on prose rather than code would fail on the very thing it is describing.
        var code = Regex.Replace(body, @"//[^\n]*", string.Empty);

        Assert.False(
            code.Contains("textContent", StringComparison.Ordinal),
            $"{script}'s escapeHtml is back to the textContent/innerHTML round-trip. That escapes what a " +
            "text node needs and leaves quotes alone, which is unsafe for the attribute interpolations " +
            "throughout these files.");

        foreach (var (character, entity) in new[]
        {
            ("&", "&amp;"), ("<", "&lt;"), (">", "&gt;"), ("\"", "&quot;"), ("'", "&#39;"),
        })
        {
            Assert.True(
                code.Contains(entity, StringComparison.Ordinal),
                $"{script}'s escapeHtml does not turn {character} into {entity}. Attribute values in this " +
                "file are built by interpolating its result, so anything it leaves intact can end the " +
                "attribute it lands in.");
        }

        // & has to go first or the entities emitted after it get their own ampersand escaped.
        var ampersand = code.IndexOf("&amp;", StringComparison.Ordinal);
        foreach (var later in new[] { "&lt;", "&gt;", "&quot;", "&#39;" })
        {
            Assert.True(
                ampersand < code.IndexOf(later, StringComparison.Ordinal),
                $"{script}'s escapeHtml replaces & after {later}, so it double-escapes its own output.");
        }
    }

    // At least one script must be checked; a path that silently found nothing would pass forever.
    [Fact]
    public void TheDashboardScriptsAreWhereThisTestExpectsThem()
    {
        Assert.True(Directory.Exists(WwwRoot()), $"{WwwRoot()} is missing - point this test at wwwroot.");
        Assert.True(DashboardScripts().Count >= 5, "Expected escapeHtml in most dashboard scripts.");
    }

    private static string WwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "SentinelCore.Web", "wwwroot");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return "";
    }
}
