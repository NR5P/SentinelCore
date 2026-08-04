using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SentinelCore.Web;

// Renders one of the markdown files in docs/ (USER_GUIDE.md, DEPLOYMENT.md, ...) to a PDF for
// the dashboard's Docs tab. Hand-rolled against Markdig's block/inline AST rather than going
// through markdown-to-HTML-to-PDF (wkhtmltopdf/headless Chromium) - this app already ships as a
// single self-contained executable meant to run with nothing else installed (see
// scripts/publish-release.sh), including on a Raspberry Pi, so a renderer that needs an external
// binary or a downloaded browser on the target machine isn't an option. QuestPDF is pure managed
// code; Program.cs registers the bundled DejaVu fonts explicitly (see Fonts/) so rendering
// doesn't depend on whatever fonts (if any) happen to be installed on the host.
public static class DocsPdfRenderer
{
    public const string BodyFont = "DejaVu Sans";
    public const string MonoFont = "DejaVu Sans Mono";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static byte[] RenderToPdf(string title, string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.6f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(10.5f).FontColor(Colors.Grey.Darken3).LineHeight(1.35f));

                page.Header().PaddingBottom(6).BorderBottom(0.75f).BorderColor(Colors.Grey.Lighten2)
                    .Text(title).FontSize(9).FontColor(Colors.Grey.Medium).SemiBold();

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8.5f).FontColor(Colors.Grey.Medium));
                    t.CurrentPageNumber();
                    t.Span(" / ");
                    t.TotalPages();
                });

                page.Content().PaddingTop(6).Column(column =>
                {
                    foreach (var block in document)
                        RenderBlock(column, block);
                });
            });
        }).GeneratePdf();
    }

    // depth tracks how many list levels this block is nested inside, purely for indentation -
    // every block type except ListBlock ignores it.
    private static void RenderBlock(ColumnDescriptor column, Block block, int depth = 0)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(column, heading);
                break;
            case ParagraphBlock paragraph:
                column.Item().PaddingBottom(5).Text(t => RenderInlines(t, paragraph.Inline));
                break;
            case Table table:
                RenderTable(column, table);
                break;
            case QuoteBlock quote:
                RenderQuote(column, quote);
                break;
            case FencedCodeBlock or CodeBlock:
                RenderCodeBlock(column, (LeafBlock)block);
                break;
            case ListBlock list:
                RenderList(column, list, depth);
                break;
            case ThematicBreakBlock:
                column.Item().PaddingVertical(8).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten2);
                break;
            // Anything else (raw HTML blocks, link reference definitions, ...) carries no
            // meaning worth reproducing in a PDF - skipped rather than crashing the render.
        }
    }

    private static void RenderHeading(ColumnDescriptor column, HeadingBlock heading)
    {
        var (fontSize, topPadding) = heading.Level switch
        {
            1 => (17f, 18f),
            2 => (14f, 16f),
            3 => (12f, 11f),
            _ => (10.5f, 8f)
        };

        // Markdig's own auto-identifier extension strips leading digits (it assumes an id can't
        // start with one), which doesn't match the "#3-the-events-tab..." anchors these docs write
        // by hand in their own table of contents. Slugify below reproduces the GitHub-style
        // algorithm those anchors actually follow instead, so TOC links resolve as real jumps.
        var slug = Slugify(PlainText(heading.Inline));
        var item = column.Item().PaddingTop(topPadding).PaddingBottom(4);
        var target = slug.Length > 0 ? item.Section(slug) : item;

        target.Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(fontSize).Bold().FontColor(Colors.Grey.Darken4));
            RenderInlines(t, heading.Inline);
        });
    }

    private static void RenderList(ColumnDescriptor column, ListBlock list, int depth)
    {
        var index = int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var entry in list)
        {
            if (entry is not ListItemBlock item) continue;
            var marker = list.IsOrdered ? $"{index++}." : "•";

            column.Item().PaddingLeft(depth * 16).Row(row =>
            {
                row.ConstantItem(18).Text(marker);
                row.RelativeItem().Column(inner =>
                {
                    foreach (var child in item)
                        RenderBlock(inner, child, depth + 1);
                });
            });
        }
    }

    private static void RenderTable(ColumnDescriptor column, Table table)
    {
        var columnCount = table.ColumnDefinitions.Count;
        if (columnCount == 0) return;

        column.Item().PaddingVertical(5).Table(t =>
        {
            t.ColumnsDefinition(columns =>
            {
                for (var i = 0; i < columnCount; i++) columns.RelativeColumn();
            });

            foreach (var rowBlock in table)
            {
                if (rowBlock is not TableRow row) continue;

                foreach (var cellBlock in row)
                {
                    if (cellBlock is not TableCell cell) continue;

                    t.Cell()
                        .Border(0.5f, Colors.Grey.Lighten2)
                        .Background(row.IsHeader ? Colors.Grey.Lighten3 : Colors.White)
                        .Padding(5)
                        .Text(cellText =>
                        {
                            if (row.IsHeader) cellText.DefaultTextStyle(x => x.Bold());
                            foreach (var cellChild in cell)
                            {
                                if (cellChild is ParagraphBlock para)
                                    RenderInlines(cellText, para.Inline);
                            }
                        });
                }
            }
        });
    }

    private static void RenderQuote(ColumnDescriptor column, QuoteBlock quote)
    {
        column.Item().PaddingVertical(4).BorderLeft(2).BorderColor(Colors.Grey.Lighten1).PaddingLeft(10)
            .Column(inner =>
            {
                foreach (var child in quote)
                    RenderBlock(inner, child);
            });
    }

    private static void RenderCodeBlock(ColumnDescriptor column, LeafBlock block)
    {
        var code = block.Lines.ToString().Replace("\r\n", "\n").TrimEnd('\n');

        column.Item().PaddingVertical(5).Background(Colors.Grey.Lighten4).Border(0.5f, Colors.Grey.Lighten2).Padding(6)
            .Text(t =>
            {
                // Small enough that DEPLOYMENT.md's widest ASCII architecture diagram line
                // (~97 columns, heavily indented) fits the page without wrapping - a wrap there
                // would silently destroy the alignment those diagrams depend on. A handful of
                // genuinely long one-off command lines elsewhere still wrap; that's fine, they
                // aren't alignment-sensitive the way the diagrams are.
                t.DefaultTextStyle(x => x.FontFamily(MonoFont).FontSize(7.5f).FontColor(Colors.Grey.Darken4));
                foreach (var line in code.Split('\n'))
                    t.Line(line.Length == 0 ? " " : line);
            });
    }

    private static void RenderInlines(TextDescriptor text, ContainerInline? container, bool bold = false, bool italic = false)
    {
        if (container is null) return;

        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    AddSpan(text, literal.Content.ToString(), bold, italic, isCode: false);
                    break;
                case CodeInline code:
                    AddSpan(text, code.Content, bold, italic, isCode: true);
                    break;
                case EmphasisInline emphasis:
                    // ** or __ (DelimiterCount 2) is bold; * or _ (1) is italic; *** (3) is both.
                    var nowBold = bold || emphasis.DelimiterCount >= 2;
                    var nowItalic = italic || emphasis.DelimiterCount is 1 or 3;
                    RenderInlines(text, emphasis, nowBold, nowItalic);
                    break;
                case LinkInline { IsImage: false } link:
                    RenderLink(text, link, bold, italic);
                    break;
                case LineBreakInline:
                    AddSpan(text, " ", bold, italic, isCode: false);
                    break;
                case ContainerInline nested:
                    RenderInlines(text, nested, bold, italic);
                    break;
            }
        }
    }

    private static void RenderLink(TextDescriptor text, LinkInline link, bool bold, bool italic)
    {
        var label = PlainText(link);
        if (label.Length == 0) label = link.Url ?? string.Empty;
        if (label.Length == 0) return;

        var url = link.Url ?? string.Empty;
        if (url.StartsWith('#'))
        {
            // The href is already the literal anchor text the doc's own TOC was written with
            // (e.g. "#3-the-events-tab--everything-the-cameras-caught") - matched against the same string
            // Slugify produces for the heading it points at, so no re-slugifying here.
            Style(text.SectionLink(label, url[1..]), bold, italic).FontColor(Colors.Blue.Darken1);
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Style(text.Hyperlink(label, url), bold, italic).FontColor(Colors.Blue.Darken1).Underline();
        }
        else
        {
            // A relative link to another doc file (e.g. "DEPLOYMENT.md") - there's no reliable
            // way to point this at that other doc's own PDF endpoint from inside a static PDF,
            // so it's rendered as plain text rather than a link that would just be broken.
            AddSpan(text, label, bold, italic, isCode: false);
        }
    }

    private static void AddSpan(TextDescriptor text, string content, bool bold, bool italic, bool isCode)
    {
        if (content.Length == 0) return;

        var span = Style(text.Span(content), bold, italic);
        if (isCode)
            span.FontFamily(MonoFont).FontColor(Colors.Red.Darken2).BackgroundColor(Colors.Grey.Lighten3);
    }

    private static TextSpanDescriptor Style(TextSpanDescriptor span, bool bold, bool italic)
    {
        if (bold) span = span.Bold();
        if (italic) span = span.Italic();
        return span;
    }

    private static string PlainText(Inline? inline)
    {
        if (inline is null) return string.Empty;

        var builder = new StringBuilder();
        Walk(inline);
        return builder.ToString();

        void Walk(Inline current)
        {
            switch (current)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content);
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline container:
                    for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                        Walk(child);
                    break;
            }
        }
    }

    // Reproduces the GitHub-flavoured-markdown heading slug algorithm: lowercase, letters/digits
    // kept as-is, any whitespace or existing hyphen becomes a hyphen, everything else (including
    // the em dashes and punctuation these docs use a lot of) is dropped outright - confirmed
    // against every anchor actually used in USER_GUIDE.md/DEPLOYMENT.md's own tables of contents.
    private static string Slugify(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            else if (char.IsWhiteSpace(ch) || ch == '-') builder.Append('-');
        }
        return builder.ToString();
    }
}
