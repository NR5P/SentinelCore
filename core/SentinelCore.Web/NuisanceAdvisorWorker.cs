using System.Net.Mail;
using SentinelCore;

namespace SentinelCore.Web;

// Looks back over each night once it is over and tells the owner when a camera has spent it
// triggering on a spider web - see LensNuisance for what that looks like in the data and why no
// amount of tuning fixes it.
//
// Runs on a slow poll rather than a timer aimed at a clock time, because the moment it wants is
// "after dawn", and dawn moves by well over an hour across the year. Each tick works out the most
// recently completed dark window, and the per-camera cooldown in the database - not a field on
// this class - is what stops the same advice going out twice.
//
// Delivery follows LoginAlertSender exactly: Email and Telegram attempted independently out of
// the main app's own configuration, one failing not blocking the other, and nothing sent at all
// if neither is configured.
public sealed class NuisanceAdvisorWorker(
    ITriggerConfigStore configStore,
    ITriggerEventStore eventStore,
    ICameraStore cameraStore,
    AppSettingsFile settings,
    IHttpClientFactory httpClientFactory,
    ILogger<NuisanceAdvisorWorker> logger) : BackgroundService
{
    private const string TelegramApiBaseUrl = "https://api.telegram.org";

    // Half an hour is plenty for something that acts once a day, and keeps the work down to a
    // handful of indexed queries per camera even on the ticks where there is nothing to do.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    // How long after dawn to wait before judging the night. Nothing depends on the delay being
    // exact; it exists so a trigger that fired minutes before first light has had time for its
    // AI verdict to come back and be written, rather than being counted as unconfirmed.
    private static readonly TimeSpan SettlingDelay = TimeSpan.FromMinutes(30);

    // The last night each camera has already been judged on, so a camera whose night was
    // unremarkable isn't re-tallied every half hour until the next dusk. Purely an optimisation -
    // correctness rests on the database, which survives a restart and this doesn't.
    private readonly Dictionary<Guid, DateOnly> _assessed = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The overnight nuisance check failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = await settings.GetSectionAsync<NuisanceAdvisorOptions>(
            NuisanceAdvisorOptions.SectionName, cancellationToken) ?? new NuisanceAdvisorOptions();
        if (!options.Enabled) return;

        var location = await settings.GetSectionAsync<LocationOptions>(
            LocationOptions.SectionName, cancellationToken) ?? new LocationOptions();

        var window = NightWindow.MostRecentlyEnded(DateTimeOffset.Now, location, SettlingDelay);
        if (window is null) return;

        var configs = await configStore.GetEnabledAsync(cancellationToken);
        foreach (var config in configs)
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (_assessed.TryGetValue(config.CameraId, out var last) && last >= window.Night) continue;
            _assessed[config.CameraId] = window.Night;

            // Without 'Send to alerts' the camera's triggers are never analysed, every row reads
            // as Confirmed by default, and there is no evidence that the night was empty rather
            // than busy. Saying nothing is the honest answer, but say why once, at debug level -
            // "why did my worst camera never get told" is otherwise unanswerable.
            if (!config.SendToAlerts)
            {
                logger.LogDebug(
                    "Skipping the overnight nuisance check for camera {CameraId}: it doesn't send triggers to alerts, so there are no AI verdicts to judge the night by.",
                    config.CameraId);
                continue;
            }

            await AssessCameraAsync(config.CameraId, window, options, cancellationToken);
        }
    }

    private async Task AssessCameraAsync(
        Guid cameraId, NightWindow window, NuisanceAdvisorOptions options, CancellationToken cancellationToken)
    {
        var camera = await cameraStore.GetAsync(cameraId, cancellationToken);
        if (camera is null) return;

        var events = await eventStore.GetAllEventsInRangeAsync(
            cameraId, window.DaylightStart, window.DarkEnd, cancellationToken);

        var finding = LensNuisance.Assess(
            cameraId, camera.Name, window.Night, events,
            window.DarkStart, window.DarkEnd, window.DaylightStart, window.DaylightEnd, options);
        if (finding is null) return;

        // Logged whether or not it can be delivered: an install with no Email or Telegram still
        // gets the finding somewhere it can be read.
        logger.LogInformation(
            "{Camera} triggered {Triggers} times between dusk and dawn on {Night} with {Confirmed} confirmed - this looks like a spider web or insects on the lens.",
            finding.CameraName, finding.Dark.Triggers, finding.Night, finding.Dark.Confirmed);

        var lastAdvised = await eventStore.GetLastNuisanceAdvisoryAsync(cameraId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (lastAdvised is not null && now - lastAdvised < TimeSpan.FromDays(options.CooldownDays))
            return;

        var sent = false;
        sent |= await TrySendEmailAsync(finding, cancellationToken);
        sent |= await TrySendTelegramAsync(finding, cancellationToken);

        // Same reasoning as LoginAlertSender: an advisory nothing could deliver shouldn't also
        // spend the cooldown that would have let tomorrow's attempt through.
        if (sent) await eventStore.RecordNuisanceAdvisoryAsync(cameraId, finding.Night, now, cancellationToken);
    }

    private async Task<bool> TrySendEmailAsync(NuisanceFinding finding, CancellationToken cancellationToken)
    {
        var email = await settings.GetSectionAsync<EmailOptions>("Email", cancellationToken);
        if (email is null || string.IsNullOrWhiteSpace(email.Username) ||
            string.IsNullOrWhiteSpace(email.AppPassword) || string.IsNullOrWhiteSpace(email.ToAddress))
        {
            logger.LogDebug("Skipping the nuisance advisory email - Email isn't configured");
            return false;
        }

        try
        {
            var fromAddress = SmtpMailer.FromAddressFor(email);
            using var message = new MailMessage
            {
                From = new MailAddress(fromAddress, email.FromName),
                Subject = NuisanceAdvisoryText.Subject(finding),
                IsBodyHtml = false,
                Body = NuisanceAdvisoryText.Body(finding)
            };
            message.To.Add(email.ToAddress);

            await SmtpMailer.SendAsync(email, message, cancellationToken);
            logger.LogInformation("Sent a nuisance advisory email for {Camera}", finding.CameraName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send the nuisance advisory email");
            return false;
        }
    }

    private async Task<bool> TrySendTelegramAsync(NuisanceFinding finding, CancellationToken cancellationToken)
    {
        var telegram = await settings.GetSectionAsync<TelegramOptions>("Telegram", cancellationToken);
        if (telegram is null || string.IsNullOrWhiteSpace(telegram.BotToken) || string.IsNullOrWhiteSpace(telegram.ChatId))
        {
            logger.LogDebug("Skipping the nuisance advisory Telegram message - Telegram isn't configured");
            return false;
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(NuisanceAdvisorWorker));
            using var response = await client.PostAsync(
                $"{TelegramApiBaseUrl}/bot{telegram.BotToken}/sendMessage",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = telegram.ChatId,
                    ["text"] = $"{NuisanceAdvisoryText.Subject(finding)}\n\n{NuisanceAdvisoryText.Body(finding)}"
                }),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("Nuisance advisory Telegram message failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return false;
            }

            logger.LogInformation("Sent a nuisance advisory Telegram message for {Camera}", finding.CameraName);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send the nuisance advisory Telegram message");
            return false;
        }
    }
}

// The dark hours of one night, and the daylight of the day it began - the two windows
// LensNuisance compares. Split out from the worker so the date arithmetic, which is the fiddly
// part, can be tested without a host.
public sealed record NightWindow(
    DateOnly Night,
    DateTimeOffset DarkStart,
    DateTimeOffset DarkEnd,
    DateTimeOffset DaylightStart,
    DateTimeOffset DaylightEnd)
{
    // Cameras switch to infrared somewhere around civil twilight, and it is the infrared that
    // gathers the insects, so that is the boundary that matters here rather than sunset.
    private const double DarknessDegrees = SolarCalculator.CivilTwilightDegrees;

    // Used when no location is configured and the sun's actual times are unknowable. Deliberately
    // conservative - well inside true darkness at any latitude this app is likely to run at - so
    // the fallback under-reports a marginal night rather than blaming a spider for dusk.
    private static readonly TimeSpan FallbackDarkStart = TimeSpan.FromHours(21);
    private static readonly TimeSpan FallbackDarkEnd = TimeSpan.FromHours(5);

    // The night that has most recently finished, or null if the one just gone hasn't yet settled.
    // `now` is local, because a night is named for the local evening it began on.
    public static NightWindow? MostRecentlyEnded(DateTimeOffset now, LocationOptions location, TimeSpan settlingDelay)
    {
        // Try last night first (dusk yesterday to dawn today); if its dawn is still too recent to
        // judge, fall back to the night before, which certainly has.
        for (var daysBack = 1; daysBack <= 2; daysBack++)
        {
            var night = DateOnly.FromDateTime(now.Date).AddDays(-daysBack);
            var window = For(night, now.Offset, location);
            if (window is not null && now >= window.DarkEnd + settlingDelay) return window;
        }

        return null;
    }

    // The windows for a named night, in the given local offset. Null only when the solar
    // calculation has no answer at all - inside a polar summer, where "after dark" is not a thing
    // that happened.
    public static NightWindow? For(DateOnly night, TimeSpan localOffset, LocationOptions location)
    {
        var coordinate = new GeoCoordinate(location.Latitude, location.Longitude);
        var morning = night.AddDays(1);

        if (coordinate.IsValid)
        {
            var dusk = SolarCalculator.Dusk(night, coordinate, DarknessDegrees);
            var dawn = SolarCalculator.Dawn(morning, coordinate, DarknessDegrees);
            var previousDawn = SolarCalculator.Dawn(night, coordinate, DarknessDegrees);
            if (dusk is not null && dawn is not null && previousDawn is not null)
                return new NightWindow(night, dusk.Value, dawn.Value, previousDawn.Value, dusk.Value);
        }

        var darkStart = new DateTimeOffset(night.ToDateTime(TimeOnly.MinValue), localOffset) + FallbackDarkStart;
        var darkEnd = new DateTimeOffset(morning.ToDateTime(TimeOnly.MinValue), localOffset) + FallbackDarkEnd;
        var daylightStart = new DateTimeOffset(night.ToDateTime(TimeOnly.MinValue), localOffset) + FallbackDarkEnd;
        return new NightWindow(night, darkStart, darkEnd, daylightStart, darkStart);
    }
}
