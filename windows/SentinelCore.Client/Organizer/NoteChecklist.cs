using System.Text.RegularExpressions;

namespace SentinelCore.Client.Organizer;

/// <summary>One line of a note body, read for its checklist item if it is one.</summary>
/// <param name="Index">The line's position in the body, split on "\n".</param>
/// <param name="Text">The whole line as written.</param>
/// <param name="InCode">True for a ``` fence line or anything between two of them.</param>
/// <param name="Done">Null when the line isn't a checklist item; otherwise whether it's ticked.</param>
/// <param name="ItemText">The item's words after the box, when it is one.</param>
public sealed record NoteLine(int Index, string Text, bool InCode, bool? Done, string? ItemText)
{
    public bool IsItem => Done is not null;
}

/// <summary>What Enter should do at the end of a line: carry <see cref="Insert"/> onto the next line, or end the list.</summary>
public sealed record NoteEnter(string? Insert, bool EndList);

/// <summary>
/// Checklists inside a note. A note stays plain Markdown - an item is a line like "- [ ] milk" or
/// "- [x] milk" - so these only rewrite lines of the body, and nothing about sync changes. They let
/// a list be built and ticked off without typing the brackets: add an item, clear the done ones,
/// turn a pasted list into checkboxes, and carry "- [ ] " onto the next line when Enter is pressed.
///
/// The dashboard (wwwroot/note-checklist.js), ios/OrganizerKit NoteChecklist.swift and android
/// organizer/NoteChecklist.kt have the same rules, and all four test suites read
/// core/docs/note-checklist-cases.json. Change them together.
/// </summary>
public static class NoteChecklist
{
    private static readonly Regex ItemLine = new(@"^(\s*)([-*])\s+\[( |x|X)\]\s+(.*)$");
    private static readonly Regex ItemForEnter = new(@"^(\s*)([-*])\s+\[( |x|X)\](\s*)(.*)$");
    private static readonly Regex Bullet = new(@"^(\s*)([-*])(\s+)(.*)$");
    private static readonly Regex Numbered = new(@"^(\s*)\d+[.)]\s+(.*)$");
    private static readonly Regex Heading = new(@"^#{1,6}\s");
    private static readonly Regex Quote = new(@"^\s*>");
    private static readonly Regex Box = new(@"\[( |x|X)\]");

    /// <summary>
    /// "\n" line ends only. A Windows text box types "\r\n", but every other surface stores "\n",
    /// so a body is normalised before it's read or saved and a line index means the same line everywhere.
    /// </summary>
    public static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static bool IsFence(string line) => line.Trim().StartsWith("```", StringComparison.Ordinal);

    private static string[] Split(string? body) => Normalize(body).Split('\n');

    /// <summary>Each line with whether it sits inside a ``` block - code is never a checklist - and its item, if any.</summary>
    public static IReadOnlyList<NoteLine> Scan(string? body)
    {
        var lines = Split(body);
        var result = new List<NoteLine>(lines.Length);
        var inCode = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index];
            if (IsFence(text))
            {
                inCode = !inCode;
                result.Add(new NoteLine(index, text, true, null, null));
                continue;
            }
            var match = inCode ? null : ItemLine.Match(text);
            result.Add(match is { Success: true }
                ? new NoteLine(index, text, false, match.Groups[3].Value != " ", match.Groups[4].Value)
                : new NoteLine(index, text, inCode, null, null));
        }
        return result;
    }

    public static (int Done, int Total) Progress(string? body)
    {
        var items = Scan(body).Where(line => line.IsItem).ToList();
        return (items.Count(line => line.Done == true), items.Count);
    }

    /// <summary>Ticks or unticks the item on that line. Anything that isn't an item is left as it was.</summary>
    public static string Toggle(string? body, int lineIndex)
    {
        var scanned = Scan(body);
        if (lineIndex < 0 || lineIndex >= scanned.Count || !scanned[lineIndex].IsItem) return Normalize(body);
        var lines = scanned.Select(line => line.Text).ToArray();
        lines[lineIndex] = Box.Replace(lines[lineIndex], match => match.Groups[1].Value == " " ? "[x]" : "[ ]", 1);
        return string.Join("\n", lines);
    }

    /// <summary>
    /// A new unticked item. It goes after the last item, so it joins the list rather than landing
    /// under whatever was written below it.
    /// </summary>
    public static string AddItem(string? body, string? text)
    {
        var clean = (text ?? "").Trim();
        var normalized = Normalize(body);
        if (clean.Length == 0) return normalized;
        var entry = "- [ ] " + clean;
        var scanned = Scan(normalized);
        var last = scanned.LastOrDefault(line => line.IsItem);
        if (last is not null)
        {
            var lines = scanned.Select(line => line.Text).ToList();
            lines.Insert(last.Index + 1, entry);
            return string.Join("\n", lines);
        }
        var trimmed = normalized.TrimEnd('\n');
        return trimmed.Trim().Length > 0 ? trimmed + "\n" + entry : entry;
    }

    public static string RemoveLine(string? body, int index)
    {
        var lines = Split(body).ToList();
        if (index < 0 || index >= lines.Count) return Normalize(body);
        lines.RemoveAt(index);
        return string.Join("\n", lines);
    }

    /// <summary>Drops every ticked item - the "done, now tidy up" button.</summary>
    public static string ClearChecked(string? body) =>
        string.Join("\n", Scan(body).Where(line => line.Done != true).Select(line => line.Text));

    /// <summary>
    /// Every plain line and bullet becomes an unticked item; headings, quotes, code and blank lines
    /// are left alone so a list under a title stays a list under a title.
    /// </summary>
    public static string MakeChecklist(string? body) =>
        string.Join("\n", Scan(body).Select(line =>
        {
            var text = line.Text;
            if (line.InCode || line.IsItem || text.Trim().Length == 0 || Heading.IsMatch(text) || Quote.IsMatch(text)) return text;
            if (Bullet.Match(text) is { Success: true } bullet) return $"{bullet.Groups[1].Value}- [ ] {bullet.Groups[4].Value}";
            if (Numbered.Match(text) is { Success: true } numbered) return $"{numbered.Groups[1].Value}- [ ] {numbered.Groups[2].Value}";
            return "- [ ] " + text.Trim();
        }));

    /// <summary>
    /// What Enter should do at the end of <paramref name="line"/>: carry the item or bullet on, end
    /// the list when the item is empty, or null for an ordinary new line.
    /// </summary>
    public static NoteEnter? ContinueOnEnter(string? line)
    {
        var text = Normalize(line);
        if (ItemForEnter.Match(text) is { Success: true } item)
        {
            if (item.Groups[4].Value.Length == 0 && item.Groups[5].Value.Length == 0) return null;
            return item.Groups[5].Value.Trim().Length > 0
                ? new NoteEnter($"{item.Groups[1].Value}{item.Groups[2].Value} [ ] ", false)
                : new NoteEnter(null, true);
        }
        if (Bullet.Match(text) is { Success: true } bullet)
        {
            return bullet.Groups[4].Value.Trim().Length > 0
                ? new NoteEnter($"{bullet.Groups[1].Value}{bullet.Groups[2].Value} ", false)
                : new NoteEnter(null, true);
        }
        return null;
    }
}
