using System.Net.Mime;
using System.Reflection;
using Markdig;

namespace SentinelCore.PushRelay;

// The relay is the one process the vendor already has on the public internet, so it also serves
// the two pages the app stores insist on: a landing page at the bare domain and the privacy
// policy. Both are rendered once at startup from the embedded markdown. Nothing here touches
// the /v1 API or its license check.
public static class PublicPages
{
    private static readonly Lazy<string> PrivacyHtml = new(() => Render(
        "Sentinel Core Privacy Policy",
        ReadEmbedded("PRIVACY_POLICY.md")));

    private static readonly Lazy<string> LandingHtml = new(() => Render(
        "Sentinel Core",
        """
        # Sentinel Core

        Self-hosted AI camera alerts. The server runs on your own computer; the apps for iPhone,
        iPad, Mac, Apple TV, Android and Windows connect to it.

        - [Privacy policy](/privacy)
        - Support: **support@nr5p.com**
        """));

    public static void MapPublicPages(this WebApplication app)
    {
        app.MapGet("/", () => Results.Content(LandingHtml.Value, MediaTypeNames.Text.Html));
        app.MapGet("/privacy", () => Results.Content(PrivacyHtml.Value, MediaTypeNames.Text.Html));
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Render(string title, string markdown)
    {
        var body = Markdown.ToHtml(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
              body { margin: 0; padding: 2rem 1rem 4rem; background: #0f1115; color: #e6e6e6;
                     font: 16px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
              main { max-width: 42rem; margin: 0 auto; }
              h1, h2 { line-height: 1.25; } h1 { font-size: 1.9rem; } h2 { margin-top: 2rem; font-size: 1.25rem; }
              a { color: #7cc4ff; } code { background: #1c2028; padding: .1em .35em; border-radius: 4px; }
              em { color: #9aa3b2; }
            </style>
            </head>
            <body><main>{{body}}</main></body>
            </html>
            """;
    }
}
