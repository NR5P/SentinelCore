namespace SentinelCore;

// Quiets the per-request logging of the two HTTP clients that exist only to poll.
//
// TelegramChatWorker long-polls getUpdates continuously and InternetOutageWorker probes
// connectivity every 60s. Neither has anything to say when the answer is "same as last time",
// but HttpClient's own handlers log "Start processing HTTP request" and "End processing HTTP
// request" at Information for every call, and do it twice over - once from LogicalHandler and
// once from ClientHandler. Measured on this box over 24h on 2026-08-25:
//
//   System.Net.Http.HttpClient.TelegramChatWorker.*     13,684 lines
//   System.Net.Http.HttpClient.InternetOutageWorker.*     5,700 lines
//   ---------------------------------------------------------------
//   the two pollers                                     19,384 of 23,408  (83% of the service)
//   SentinelCore.AlertProcessor                             707
//
// That is not just volume, it is volume that hides things. The lines worth reading - an alert
// being analysed, a retention sweep, a notification that failed to send - arrive outnumbered
// twenty-seven to one by two workers reporting that nothing happened, which is the opposite of
// what the journal is for. It is also the reason `journalctl -p warning` is currently the only
// practical way to read this service, and that view drops every Information line worth having.
//
// Warning rather than IHttpClientBuilder.RemoveAllLoggers(), which would also be a one-liner:
// removing the loggers would take genuine HTTP-level failures with it. At Warning the routine
// pair goes away and anything actually wrong still lands. Nothing diagnostic is lost either way,
// because both workers already log their own failures themselves - TelegramChatWorker's "Failed
// to poll Telegram for updates" is what tells you the poll broke, not the handler's Information
// line, and that warning is unaffected by this filter (different category: SentinelCore.*).
//
// Deliberately scoped to these two named clients rather than the whole
// "System.Net.Http.HttpClient" prefix, which would be the tempting single rule. OllamaVisionClient
// logs ~520 lines a day and those are how inference latency is measured; the alert channels log
// ~32 each and those are how a dropped notification gets traced. Both stay at Information. Only
// clients whose traffic is pure heartbeat belong in this list.
public static class PollingHttpClientLogging
{
    // The logger filter matches on category prefix, so one entry per client covers both the
    // .LogicalHandler and .ClientHandler categories the HttpClientFactory derives from the name.
    // Built from nameof() because that is also what the AddHttpClient(nameof(...)) registrations
    // in Program.cs pass as the client name - renaming either worker keeps the two in step
    // instead of silently un-quieting it.
    public static readonly string[] QuietedCategories =
    [
        "System.Net.Http.HttpClient." + nameof(TelegramChatWorker),
        "System.Net.Http.HttpClient." + nameof(InternetOutageWorker),
    ];

    public static ILoggingBuilder AddPollingHttpClientFilters(this ILoggingBuilder logging)
    {
        foreach (var category in QuietedCategories)
            logging.AddFilter(category, LogLevel.Warning);

        return logging;
    }
}
