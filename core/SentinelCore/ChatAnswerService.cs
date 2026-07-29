using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Shared Q&A logic used by both the Telegram bot (TelegramChatWorker) and the dashboard's own
// ask box (SentinelCore.Web/ChatEndpoints.cs, reached through POST /api/chat/ask below)
// - one place building context and calling Ollama, so the two entry points never drift apart.
public sealed class ChatAnswerService
{
    private readonly IEventStore _eventStore;
    private readonly IOllamaChatClient _chatClient;
    private readonly IOptionsMonitor<TelegramChatOptions> _chatOptionsMonitor;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ChatAnswerService> _logger;

    public ChatAnswerService(
        IEventStore eventStore,
        IOllamaChatClient chatClient,
        IOptionsMonitor<TelegramChatOptions> chatOptions,
        IHostEnvironment environment,
        ILogger<ChatAnswerService> logger)
    {
        _eventStore = eventStore;
        _chatClient = chatClient;
        _chatOptionsMonitor = chatOptions;
        _environment = environment;
        _logger = logger;
    }

    // Tried combining the full app documentation with the event log in a single prompt for
    // every question - on a small local model over CPU, ~34KB of documentation stacked on the
    // event log was slow enough (2+ minutes) to blow past the request timeout outright on some
    // questions, and on others the model quietly lost track of the event log and answered from
    // the docs alone instead. Routing each question to exactly one context source instead fixes
    // both: event questions stay as fast as they were before this feature existed, and how-to
    // questions get the full documentation without competing against event history for the
    // model's attention. The cost is a plain keyword heuristic instead of true intent
    // detection - it won't be perfect on a question that genuinely needs both, but it's right
    // for the overwhelming majority of real questions in either direction.
    private static readonly string[] HowToKeywords =
    [
        "how do i", "how to", "how can i", "how does", "turn on", "turn off", "enable", "disable",
        "configure", "configuration", "setting", "settings", "set up", "setup", "where is",
        "where can i", "what is", "what does", "explain", "documentation", "feature", "option"
    ];

    public async Task<string> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        var chatOptions = _chatOptionsMonitor.CurrentValue;
        if (!chatOptions.Enabled)
            return "Chat is turned off - enable it in Settings under Telegram Chat.";

        var systemPrompt = LooksLikeHowToQuestion(question)
            ? BuildDocsPrompt(question)
            : await BuildEventLogPromptAsync(chatOptions, cancellationToken);

        var answer = await _chatClient.AskAsync(systemPrompt, question, cancellationToken);
        return answer.Length > 4000 ? answer[..4000] : answer;
    }

    private static bool LooksLikeHowToQuestion(string question)
    {
        var lowered = question.ToLowerInvariant();
        return HowToKeywords.Any(lowered.Contains);
    }

    private async Task<string> BuildEventLogPromptAsync(TelegramChatOptions chatOptions, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var events = await _eventStore.GetEventsAsync(now.AddDays(-chatOptions.LookbackDays), now, cancellationToken);

        var recent = events
            .Where(e => string.Equals(e.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.OccurredAt)
            .Take(chatOptions.MaxEventsInContext)
            .OrderBy(e => e.OccurredAt)
            .ToList();

        // Same day-of-week + ISO-date + 24h-time format BuildEventLog uses for each event line -
        // a small local model asked "did this happen today" has to compare this date against
        // each log line's date, and it's much more reliable at getting that right when both
        // sides are the exact same string shape than when it has to parse two different date
        // formats (confirmed the hard way: with mismatched formats, it once answered about
        // yesterday's events as if they were today's).
        return $"""
            You are an assistant for a home security AI camera system called Sentinel Core. Today's date
            and time is {now:dddd yyyy-MM-dd HH:mm} ({now:zzz} offset). Answer the user's question using
            ONLY the event log below, which covers the last {chatOptions.LookbackDays} day(s) up to now -
            each line starts with its own date in the same format as today's date above. When the
            question refers to "today", "yesterday", or a specific day, compare the log line's date
            against today's date above exactly - don't assume every line in the log happened today.
            Mention every relevant event in the log, not just the first one. Reply concisely and
            conversationally, no markdown. If the log doesn't contain the answer, say so plainly instead
            of guessing.

            Event log:
            {TelegramChatWorker.BuildEventLog(recent)}
            """;
    }

    private string BuildDocsPrompt(string question) => $"""
        You are an assistant for a home security AI camera system called Sentinel Core. Answer the
        user's question about how to use or configure the app using ONLY the documentation excerpt
        below (pulled from the full User Guide based on the question). Reply concisely and
        conversationally, no markdown. If the excerpt doesn't cover it, say so plainly instead of
        guessing.

        App documentation excerpt:
        {SelectRelevantSections(ReadDocs(), question)}
        """;

    private static readonly string[] Stopwords =
    [
        "the", "a", "an", "is", "are", "do", "does", "did", "to", "of", "in", "on", "for", "and",
        "or", "what", "how", "this", "that", "my", "your", "can", "you", "me", "about"
    ];

    // The full User Guide is ~34KB - confirmed too much for a small local model to process
    // quickly even alone (still took over 3 minutes on modest CPU hardware). Splitting it into
    // its numbered sections and keeping only the ones that actually share words with the
    // question keeps the prompt small while still surfacing genuinely relevant content, rather
    // than an arbitrary character cutoff that would randomly favor whichever sections happen to
    // come first regardless of what was actually asked.
    private static string SelectRelevantSections(string docs, string question)
    {
        if (docs == "(no documentation available)") return docs;

        var sections = Regex.Split(docs, @"(?=^## \d+\.)", RegexOptions.Multiline)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        if (sections.Count == 0) return docs;

        var questionWords = Regex.Matches(question.ToLowerInvariant(), "[a-z]{3,}")
            .Select(m => m.Value)
            .Where(w => !Stopwords.Contains(w))
            .ToHashSet();

        // A heading match is a far stronger topical signal than a body match - every section
        // briefly cross-references several others (e.g. "Known Vehicles... see section 10"),
        // which without this weighting let a long, loosely-related section (Settings, which
        // mentions nearly everything in passing) regularly outscore the one section that's
        // actually about the question.
        var scored = sections
            .Select(section =>
            {
                var newlineIndex = section.IndexOf('\n');
                var heading = newlineIndex < 0 ? section : section[..newlineIndex];
                var headingMatches = questionWords.Count(w => heading.Contains(w, StringComparison.OrdinalIgnoreCase));
                var bodyMatches = questionWords.Count(w => section.Contains(w, StringComparison.OrdinalIgnoreCase));
                return (section, score: headingMatches * 5 + bodyMatches);
            })
            .OrderByDescending(x => x.score)
            .ToList();

        var top = scored.Take(2).Where(x => x.score > 0).Select(x => x.section).ToList();
        // Nothing shared even one word with the question - fall back to the overview section
        // (always first) rather than guessing further.
        if (top.Count == 0) top = [sections[0]];

        // A firm ceiling on top of picking fewer/smaller sections - some sections (Settings,
        // Prompt Profiles, rate-limiting) are individually large enough that even one or two of
        // them can still be a lot for this model. Trimming the lowest-priority section rather
        // than the highest-priority one keeps whatever's most relevant intact.
        const int maxCombinedLength = 4000;
        var combined = string.Join("\n", top);
        return combined.Length > maxCombinedLength ? combined[..maxCombinedLength] : combined;
    }

    // Read fresh on every question rather than cached - matches this app's existing "no
    // caching, always re-verify" approach for other optional file-backed context (LicenseStatus,
    // AppSettingsFile). A missing/unreadable guide never fails the question, it's just absent
    // from the context the model sees.
    private string ReadDocs()
    {
        try
        {
            var path = Path.Combine(_environment.ContentRootPath, "..", "docs", "USER_GUIDE.md");
            return File.Exists(path) ? File.ReadAllText(path) : "(no documentation available)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read USER_GUIDE.md for chat context - continuing without it.");
            return "(no documentation available)";
        }
    }
}
