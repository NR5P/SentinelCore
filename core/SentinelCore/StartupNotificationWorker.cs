using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// What the previous run left behind. LastSeenUtc is refreshed on a timer while healthy;
// CleanShutdown is only ever true if the process got the chance to stop gracefully - which is
// precisely what a power cut denies it.
public sealed record HeartbeatState(DateTimeOffset LastSeenUtc, DateTimeOffset StartedUtc, bool CleanShutdown);

public enum OutageKind
{
    // Nothing to report: first run, unreadable state, too short to care about, or a clock that
    // moved backwards.
    None,

    // No clean shutdown was recorded, so the process was killed rather than asked to stop.
    Unexpected,

    // The previous run stopped gracefully. Still worth an email if it was down a while, but it
    // was somebody's decision, not a fault.
    Clean
}

public sealed record StartupDiagnosis(OutageKind Kind, TimeSpan Downtime, DateTimeOffset? DownSinceUtc)
{
    public bool ShouldNotify => Kind is not OutageKind.None;

    public static readonly StartupDiagnosis Nothing = new(OutageKind.None, TimeSpan.Zero, null);

    // Past this, a bad clock is far likelier than a real outage. A machine genuinely switched off
    // for over a year gets no email, which is a fair trade against emailing "down for 1,400 days"
    // and looking broken.
    public static readonly TimeSpan MaximumPlausibleDowntime = TimeSpan.FromDays(365);

    // Pure and side-effect free so every branch below is testable without a filesystem or an SMTP
    // server. The worker only supplies the inputs.
    public static StartupDiagnosis Evaluate(HeartbeatState? previous, DateTimeOffset nowUtc, TimeSpan minimumDowntime)
    {
        // No previous state means a fresh install, not an outage. Emailing here would greet every
        // new customer with a fault report.
        if (previous is null) return Nothing;

        var downtime = nowUtc - previous.LastSeenUtc;

        // A machine whose RTC battery is flat boots with a badly wrong clock and only corrects
        // itself once NTP syncs - and losing power is exactly when that shows up. A clock that
        // booted *ahead* of real time is the dangerous direction: it would otherwise produce a
        // confident email claiming the server had been down for years.
        if (downtime > MaximumPlausibleDowntime) return Nothing;

        // Redeploy restarts take seconds. Without this the feature would email on every deploy.
        // This also absorbs the opposite clock fault: a gap that came out negative because the
        // clock moved backwards is below any threshold, so it reports nothing.
        if (downtime < minimumDowntime) return Nothing;

        var kind = previous.CleanShutdown ? OutageKind.Clean : OutageKind.Unexpected;
        return new StartupDiagnosis(kind, downtime, previous.LastSeenUtc);
    }
}

// Records that the service is alive, so that the *next* start can notice a gap and report it.
//
// A server that's down can't email anyone, so an outage can only ever be reported retrospectively,
// on the way back up. That's the whole shape of this feature: leave a breadcrumb on a timer while
// healthy, and read it on the next boot.
//
// Only registered in the main app. All three services would each notice the same reboot, and three
// emails per power cut is noise - and the main app is where the SMTP settings already live.
public sealed class StartupNotificationWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IOptionsMonitor<StartupNotificationOptions> _options;
    private readonly IOptionsMonitor<EmailOptions> _emailOptions;
    private readonly ILogger<StartupNotificationWorker> _logger;
    private readonly string _heartbeatPath;
    private readonly string _temporaryPath;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    // StopAsync can fire while the heartbeat loop is mid-write. Without this they interleave
    // through the same temp file and the result that lands is corrupt JSON - which the next start
    // then can't read, silently losing the outage report it exists to produce.
    private readonly Lock _writeLock = new();

    // Set the moment shutdown begins. Guarded by _writeLock rather than volatile because the
    // check that reads it has to be atomic with the write it protects.
    private bool _stopping;

    public StartupNotificationWorker(
        IOptionsMonitor<StartupNotificationOptions> options,
        IOptionsMonitor<EmailOptions> emailOptions,
        IOptionsMonitor<BridgeOptions> bridgeOptions,
        IHostEnvironment environment,
        ILogger<StartupNotificationWorker> logger)
    {
        _options = options;
        _emailOptions = emailOptions;
        _logger = logger;

        // Same resolution rule as FileResultStore/SqliteEventStore so the heartbeat lands beside
        // the rest of the data. RetentionWorker only sweeps images/ and results/, so nothing
        // prunes this out from under us.
        var configured = bridgeOptions.CurrentValue.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        _heartbeatPath = Path.Combine(root, "heartbeat.json");

        // Process-scoped so a second instance pointed at the same data directory can't scribble
        // over this one's half-written file.
        _temporaryPath = $"{_heartbeatPath}.{Environment.ProcessId}.tmp";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read before the heartbeat loop below overwrites it. Done here rather than in StartAsync
        // so a slow or unreachable SMTP server can't hold up service startup.
        var previous = ReadHeartbeat();

        var options = _options.CurrentValue;
        if (options.Enabled)
        {
            var diagnosis = StartupDiagnosis.Evaluate(
                previous, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(options.MinimumDowntimeMinutes));

            if (diagnosis.ShouldNotify)
                await SendOutageEmailAsync(diagnosis, stoppingToken);
        }

        WriteHeartbeat(cleanShutdown: false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(_options.CurrentValue.HeartbeatIntervalSeconds);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            WriteHeartbeat(cleanShutdown: false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The marker that separates "somebody stopped it" from "the power died". Written on the
        // way out, which is the one thing an unexpected shutdown never gets to do.
        WriteHeartbeat(cleanShutdown: true);
        await base.StopAsync(cancellationToken);

        // Deliberately after the base call, which waits for ExecuteAsync to unwind: by here no
        // further heartbeat can be in flight, so the clean marker written above is the last word.
        // The flag itself is what makes that true - see WriteHeartbeat.
    }

    private HeartbeatState? ReadHeartbeat()
    {
        if (!File.Exists(_heartbeatPath)) return null;

        try
        {
            var json = File.ReadAllText(_heartbeatPath);
            return JsonSerializer.Deserialize<HeartbeatState>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Treated as "no previous state" rather than as an outage: a corrupt file tells us
            // nothing about how long the service was down, and an email quoting a made-up duration
            // is worse than staying quiet.
            _logger.LogWarning(ex, "Couldn't read the heartbeat file at {Path}; skipping the outage check for this start", _heartbeatPath);
            return null;
        }
    }

    private void WriteHeartbeat(bool cleanShutdown)
    {
        var state = new HeartbeatState(DateTimeOffset.UtcNow, _startedUtc, cleanShutdown);

        try
        {
            lock (_writeLock)
            {
                // Once shutdown has begun the clean marker is final. Without this, the heartbeat
                // still unwinding on the worker thread can land *after* StopAsync's write and
                // re-mark the run as unclean - which would report a deliberate stop as a power cut
                // on the next start. Nothing orders those two writers, so this is what orders them.
                if (_stopping && !cleanShutdown) return;
                _stopping |= cleanShutdown;

                // Temp-then-move, because a power cut mid-write is the exact scenario this feature
                // exists for - a half-written file would corrupt the one thing meant to detect it.
                File.WriteAllText(_temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(_temporaryPath, _heartbeatPath, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never fatal. Losing a heartbeat costs us outage detection, not alert processing.
            _logger.LogWarning(ex, "Couldn't write the heartbeat file at {Path}", _heartbeatPath);
        }
    }

    private async Task SendOutageEmailAsync(StartupDiagnosis diagnosis, CancellationToken cancellationToken)
    {
        var email = _emailOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(email.Username) ||
            string.IsNullOrWhiteSpace(email.AppPassword) ||
            string.IsNullOrWhiteSpace(email.ToAddress))
        {
            _logger.LogWarning(
                "Server was down for {Downtime} ({Kind}) but no outage email could be sent - Email settings aren't configured",
                OutageEmail.Describe(diagnosis.Downtime), diagnosis.Kind);
            return;
        }

        try
        {
            using var message = OutageEmail.Build(email, diagnosis);
            await SmtpMailer.SendAsync(email, message, cancellationToken);
            _logger.LogInformation(
                "Sent an outage email: server was down for {Downtime} ({Kind})",
                OutageEmail.Describe(diagnosis.Downtime), diagnosis.Kind);
        }
        catch (Exception ex)
        {
            // A mail failure must never stop the service from coming up.
            _logger.LogWarning(ex, "Failed to send the outage email");
        }
    }
}

public static class OutageEmail
{
    // Deliberately goes to Email:ToAddress - the same inbox as the alerts themselves, which is
    // what makes this useful. Quiet hours are not consulted: they suppress routine alerts, and an
    // outage notice is the opposite of routine.
    public static MailMessage Build(EmailOptions email, StartupDiagnosis diagnosis)
    {
        var fromAddress = SmtpMailer.FromAddressFor(email);
        var duration = Describe(diagnosis.Downtime);

        var cause = diagnosis.Kind == OutageKind.Unexpected
            ? "No clean shutdown was recorded, so this looks like a power cut, a crash, or the machine being reset."
            : "The previous run shut down cleanly, so this was almost certainly a deliberate stop, reboot, or update.";

        var downSince = diagnosis.DownSinceUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "unknown";

        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, email.FromName),
            Subject = diagnosis.Kind == OutageKind.Unexpected
                ? $"Sentinel Core is back online after {duration} down"
                : $"Sentinel Core is back online after a planned {duration} stop",
            IsBodyHtml = false,
            Body = $"""
                Sentinel Core has started up again on {Environment.MachineName}.

                Down for at least: {duration}
                Last seen alive:   {downSince}
                Back online:       {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}

                {cause}

                Camera alerts were not being analysed during this period. Anything Blue Iris tried
                to send while the service was down did not reach it.

                "At least" is literal: the time above is measured from the last recorded heartbeat,
                so the real outage may have been up to a minute longer.
                """
        };

        message.To.Add(email.ToAddress);
        return message;
    }

    // Rounded to whole units on purpose - "2 hours 14 minutes" is what you want to read at a
    // glance, not a duration carried out to the millisecond.
    public static string Describe(TimeSpan downtime)
    {
        if (downtime.TotalMinutes < 1) return $"{Math.Max(1, (int)downtime.TotalSeconds)} seconds";
        if (downtime.TotalHours < 1) return Plural((int)downtime.TotalMinutes, "minute");
        if (downtime.TotalDays < 1)
        {
            var hours = Plural((int)downtime.TotalHours, "hour");
            var minutes = downtime.Minutes;
            return minutes == 0 ? hours : $"{hours} {Plural(minutes, "minute")}";
        }

        var days = Plural((int)downtime.TotalDays, "day");
        return downtime.Hours == 0 ? days : $"{days} {Plural(downtime.Hours, "hour")}";
    }

    private static string Plural(int value, string unit) => $"{value} {unit}{(value == 1 ? "" : "s")}";
}
