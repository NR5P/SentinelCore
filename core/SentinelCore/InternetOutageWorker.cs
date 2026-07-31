using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// Written to disk the moment an outage is declared and deleted the moment it ends, so an outage
// that spans a restart still gets reported. DownSinceUtc is the first *failed* probe, not the
// point the outage was declared - the debounce shouldn't inflate the number we report.
public sealed record InternetOutageState(DateTimeOffset DownSinceUtc, DateTimeOffset DeclaredUtc);

public sealed record InternetOutageReport(TimeSpan Downtime, DateTimeOffset DownSinceUtc)
{
    // Pure and side-effect free, like StartupDiagnosis.Evaluate, so every branch is testable
    // without a clock, a filesystem, or a network. Null means "nothing worth telling them about".
    public static InternetOutageReport? Evaluate(
        InternetOutageState? outage, DateTimeOffset nowUtc, TimeSpan minimumOutage)
    {
        if (outage is null) return null;

        var downtime = nowUtc - outage.DownSinceUtc;

        // Same guard as the power-cut path, for the same reason: a machine whose clock jumped
        // (flat RTC battery, NTP finally syncing after the link returned - which is precisely
        // when that happens) must not produce a confident "offline for 900 days" email.
        if (downtime > StartupDiagnosis.MaximumPlausibleDowntime) return null;

        // Also absorbs a clock that moved backwards: a negative gap is below any threshold.
        if (downtime < minimumOutage) return null;

        return new InternetOutageReport(downtime, outage.DownSinceUtc);
    }
}

// Notices when the internet connection drops and reports it once it comes back.
//
// The shape is forced by the problem: a box with no route out can't tell anyone it has no route
// out, so - exactly like the power-cut feature - this can only ever report retrospectively. What
// it adds over StartupNotificationWorker is that the process stays alive throughout, so the
// outage can be measured directly rather than inferred from a gap in heartbeats.
//
// Off by default. Plenty of installs are deliberately LAN-only, and those must not be told their
// isolated network is broken every hour.
//
// Registered only in the main app, where the Email and Telegram settings already live.
public sealed class InternetOutageWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string TelegramApiBaseUrl = "https://api.telegram.org";

    // A link that has just come back is not immediately usable: DNS caches are cold and routes
    // are still settling, so the first SMTP or Telegram attempt can fail on an otherwise healthy
    // connection. Giving up after one try would lose the whole notification the feature exists to
    // send.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45)
    ];

    private readonly IOptionsMonitor<InternetOutageOptions> _options;
    private readonly IOptionsMonitor<EmailOptions> _emailOptions;
    private readonly IOptionsMonitor<TelegramOptions> _telegramOptions;
    private readonly HttpClient _httpClient;
    private readonly ILogger<InternetOutageWorker> _logger;
    private readonly string _statePath;
    private readonly string _temporaryPath;

    // The outage currently in progress, if any. Loaded from disk at startup so a restart in the
    // middle of one doesn't lose it.
    private InternetOutageState? _outage;

    // When probes started failing. Distinct from _outage: this is set on the very first failure,
    // while _outage only exists once the failures have persisted long enough to count.
    private DateTimeOffset? _firstFailureUtc;

    // A tracker's threshold is fixed at construction, so it's rebuilt when the setting changes -
    // same approach as SelfMonitoringWorker.
    private DegradationTracker _tracker;
    private int _trackerThreshold;

    public InternetOutageWorker(
        IOptionsMonitor<InternetOutageOptions> options,
        IOptionsMonitor<EmailOptions> emailOptions,
        IOptionsMonitor<TelegramOptions> telegramOptions,
        IOptionsMonitor<BridgeOptions> bridgeOptions,
        IHttpClientFactory httpClientFactory,
        IHostEnvironment environment,
        ILogger<InternetOutageWorker> logger)
    {
        _options = options;
        _emailOptions = emailOptions;
        _telegramOptions = telegramOptions;
        _httpClient = httpClientFactory.CreateClient(nameof(InternetOutageWorker));
        _logger = logger;

        // Beside heartbeat.json, by the same resolution rule, for the same reason: RetentionWorker
        // only sweeps images/ and results/, so nothing prunes this out from under us.
        var configured = bridgeOptions.CurrentValue.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _statePath = Path.Combine(root, "internet-outage.json");
        _temporaryPath = $"{_statePath}.{Environment.ProcessId}.tmp";

        _trackerThreshold = options.CurrentValue.ConsecutiveFailuresBeforeAlert;
        _tracker = new DegradationTracker(_trackerThreshold);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read before the loop can overwrite it. An outage still open in this file means the
        // process restarted mid-outage; the first successful probe below will report it.
        _outage = ReadState();

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                // Turning the feature off forgets any outage in progress. Otherwise switching it
                // back on later would email about a gap nobody had asked to be watched.
                if (_outage is not null)
                {
                    _outage = null;
                    ClearState();
                }

                _firstFailureUtc = null;
                if (!await WaitAsync(TimeSpan.FromSeconds(60), stoppingToken)) break;
                continue;
            }

            RebuildTrackerIfThresholdChanged(options);

            try
            {
                await RunCheckAsync(options, stoppingToken);
            }
            catch (Exception ex)
            {
                // Never let a check cycle take the service down - this is a reporting feature.
                _logger.LogError(ex, "An internet connectivity check failed unexpectedly");
            }

            if (!await WaitAsync(TimeSpan.FromSeconds(options.CheckIntervalSeconds), stoppingToken)) break;
        }
    }

    private void RebuildTrackerIfThresholdChanged(InternetOutageOptions options)
    {
        if (options.ConsecutiveFailuresBeforeAlert == _trackerThreshold) return;

        _trackerThreshold = options.ConsecutiveFailuresBeforeAlert;
        _tracker = new DegradationTracker(_trackerThreshold);
    }

    private async Task RunCheckAsync(InternetOutageOptions options, CancellationToken cancellationToken)
    {
        var online = await IsOnlineAsync(options, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (!online)
        {
            _firstFailureUtc ??= now;

            // The _outage null check matters on the restart path: the tracker starts fresh in a
            // new process and will re-declare, but the persisted DownSinceUtc is the true start
            // of the outage and must not be replaced with this process's start.
            if (_tracker.Observe(isHealthy: false) == DegradationTransition.Alert && _outage is null)
            {
                _outage = new InternetOutageState(_firstFailureUtc.Value, now);
                WriteState(_outage);

                // Logged, not sent: there is by definition no way to send anything right now.
                _logger.LogWarning(
                    "The internet connection has been unreachable since {DownSince}. Nothing can be sent out until it returns; a report will follow then.",
                    _outage.DownSinceUtc.ToLocalTime());
            }

            return;
        }

        _tracker.Observe(isHealthy: true);
        _firstFailureUtc = null;

        if (_outage is null) return;

        // Cleared before notifying, not after: a notification that fails must not leave the
        // outage open to be reported all over again on the next successful probe.
        var outage = _outage;
        _outage = null;
        ClearState();

        var report = InternetOutageReport.Evaluate(
            outage, now, TimeSpan.FromMinutes(options.MinimumOutageMinutes));

        if (report is null)
        {
            _logger.LogInformation(
                "The internet connection returned after a short drop (since {DownSince}); below the reporting threshold",
                outage.DownSinceUtc.ToLocalTime());
            return;
        }

        _logger.LogWarning(
            "The internet connection returned after being down for at least {Downtime}",
            OutageEmail.Describe(report.Downtime));

        await NotifyAsync(report, cancellationToken);
    }

    // Any answer from any probe proves packets are leaving the building - a 403 or a 500 from a
    // real server is as good a proof of connectivity as a 204. Only a transport failure against
    // every probe counts as an outage, so one endpoint being down doesn't read as the internet
    // being down.
    private async Task<bool> IsOnlineAsync(InternetOutageOptions options, CancellationToken cancellationToken)
    {
        var urls = options.ProbeUrls.Count > 0 ? options.ProbeUrls : InternetOutageOptions.DefaultProbeUrls;

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ProbeTimeoutSeconds));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
            {
                // Shutdown is not an outage.
                cancellationToken.ThrowIfCancellationRequested();

                // Debug: while the link is genuinely down this runs every interval, and a warning
                // per probe per minute would bury everything else in the log.
                _logger.LogDebug(ex, "Connectivity probe to {Url} failed", url);
            }
        }

        return false;
    }

    private async Task NotifyAsync(InternetOutageReport report, CancellationToken cancellationToken)
    {
        var sent = false;

        var email = _emailOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(email.Username) ||
            string.IsNullOrWhiteSpace(email.AppPassword) ||
            string.IsNullOrWhiteSpace(email.ToAddress))
        {
            _logger.LogInformation("Skipping the internet outage email - Email settings aren't configured");
        }
        else
        {
            sent |= await SendWithRetriesAsync(
                "internet outage email",
                ct => SendEmailAsync(email, report, ct),
                cancellationToken);
        }

        var telegram = _telegramOptions.CurrentValue;
        if (!telegram.Enabled ||
            string.IsNullOrWhiteSpace(telegram.BotToken) ||
            string.IsNullOrWhiteSpace(telegram.ChatId))
        {
            _logger.LogInformation("Skipping the internet outage Telegram message - Telegram isn't configured");
        }
        else
        {
            sent |= await SendWithRetriesAsync(
                "internet outage Telegram message",
                ct => SendTelegramAsync(telegram, report, ct),
                cancellationToken);
        }

        if (!sent)
            _logger.LogWarning(
                "The internet was down for at least {Downtime} but no outage notification could be delivered",
                OutageEmail.Describe(report.Downtime));
    }

    private async Task<bool> SendWithRetriesAsync(
        string description, Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            if (RetryDelays[attempt] > TimeSpan.Zero && !await WaitAsync(RetryDelays[attempt], cancellationToken))
                return false;

            try
            {
                await send(cancellationToken);
                _logger.LogInformation("Sent the {Description}", description);
                return true;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) return false;

                // Only the last failure is worth a warning; the earlier ones are expected while
                // the connection finishes coming up.
                if (attempt == RetryDelays.Length - 1)
                    _logger.LogWarning(ex, "Failed to send the {Description} after {Attempts} attempts", description, RetryDelays.Length);
                else
                    _logger.LogDebug(ex, "Attempt {Attempt} to send the {Description} failed; retrying", attempt + 1, description);
            }
        }

        return false;
    }

    private static async Task SendEmailAsync(
        EmailOptions email, InternetOutageReport report, CancellationToken cancellationToken)
    {
        using var message = InternetOutageNotification.BuildEmail(email, report);
        using var client = new SmtpClient(email.SmtpHost, email.SmtpPort)
        {
            EnableSsl = email.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(email.Username, email.AppPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        await client.SendMailAsync(message).WaitAsync(cancellationToken);
    }

    private async Task SendTelegramAsync(
        TelegramOptions telegram, InternetOutageReport report, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(
            $"{TelegramApiBaseUrl}/bot{telegram.BotToken}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = telegram.ChatId,
                ["text"] = InternetOutageNotification.BuildTelegramText(report)
            }),
            cancellationToken);

        // Telegram answers 200 with ok:false for a bad token or chat id, so a non-success status
        // is the only thing worth retrying on - and it must throw to be retried at all.
        response.EnsureSuccessStatusCode();
    }

    private InternetOutageState? ReadState()
    {
        if (!File.Exists(_statePath)) return null;

        try
        {
            return JsonSerializer.Deserialize<InternetOutageState>(File.ReadAllText(_statePath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt file tells us nothing about how long the link was down, and a report
            // quoting a made-up duration is worse than staying quiet.
            _logger.LogWarning(ex, "Couldn't read the internet outage state at {Path}; ignoring it", _statePath);
            return null;
        }
    }

    private void WriteState(InternetOutageState state)
    {
        try
        {
            // Temp-then-move: an outage is exactly when the machine is most likely to also lose
            // power, and a half-written file would lose the report this exists to produce.
            File.WriteAllText(_temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(_temporaryPath, _statePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Costs us only the ability to report an outage that spans a restart.
            _logger.LogWarning(ex, "Couldn't write the internet outage state at {Path}", _statePath);
        }
    }

    private void ClearState()
    {
        try
        {
            File.Delete(_statePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale file would re-report this same outage after the next restart. Not silent:
            // if it happens, the log is how you'd explain the duplicate.
            _logger.LogWarning(ex, "Couldn't clear the internet outage state at {Path}", _statePath);
        }
    }

    private static async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

public static class InternetOutageNotification
{
    // Goes to Email:ToAddress and the alert chat - the same places the alerts themselves land,
    // which is what makes this useful. Quiet hours aren't consulted: they raise the bar for
    // routine alerts, and "you were unreachable for three hours" is not routine.
    public static MailMessage BuildEmail(EmailOptions email, InternetOutageReport report)
    {
        var fromAddress = string.IsNullOrWhiteSpace(email.FromAddress) ? email.Username : email.FromAddress;
        var duration = OutageEmail.Describe(report.Downtime);

        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, email.FromName),
            Subject = $"Internet is back after {duration} offline",
            IsBodyHtml = false,
            Body = $"""
                Sentinel Core's internet connection has come back on {Environment.MachineName}.

                Offline for at least: {duration}
                Connection lost:      {report.DownSinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}
                Back online:          {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}

                Sentinel Core itself kept running throughout - your cameras and the Feed were not
                affected. What couldn't happen is anything that has to leave the building: email,
                Telegram and push notifications, and cloud AI analysis if that's your vision
                backend. Any notification that would have been sent during the outage was not
                delivered, and is not re-sent now - check the Feed for what you missed.

                "At least" is literal: the outage is measured from the first failed connectivity
                check, so it may have started up to one check interval earlier.
                """
        };

        message.To.Add(email.ToAddress);
        return message;
    }

    public static string BuildTelegramText(InternetOutageReport report) =>
        $"""
        Internet is back after at least {OutageEmail.Describe(report.Downtime)} offline.

        Connection lost: {report.DownSinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}
        Back online: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}

        Sentinel Core kept running, but nothing could be sent while the link was down. Any alerts from that window were not delivered and are not re-sent - check the Feed.
        """;
}
