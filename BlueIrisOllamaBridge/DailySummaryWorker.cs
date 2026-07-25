using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public enum DigestKind
{
    Morning,
    Evening
}

// Sends two scheduled recap emails to the same address alert emails already go to:
// an evening digest of the day's activity, and a morning digest of what happened overnight.
// The two windows partition a full day with no overlap (see DailySummaryOptions).
public sealed class DailySummaryWorker : BackgroundService
{
    private readonly IEventStore _eventStore;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly IOptionsMonitor<DailySummaryOptions> _optionsMonitor;
    private readonly IOptionsMonitor<EmailOptions> _emailOptionsMonitor;
    private readonly ILogger<DailySummaryWorker> _logger;

    public DailySummaryWorker(
        IEventStore eventStore,
        IImageOptimizer imageOptimizer,
        IOptionsMonitor<DailySummaryOptions> options,
        IOptionsMonitor<EmailOptions> emailOptions,
        ILogger<DailySummaryWorker> logger)
    {
        _eventStore = eventStore;
        _imageOptimizer = imageOptimizer;
        _optionsMonitor = options;
        _emailOptionsMonitor = emailOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue;
            if (!options.Enabled)
            {
                if (!await WaitAsync(TimeSpan.FromSeconds(60), stoppingToken)) break;
                continue;
            }

            var emailOptions = _emailOptionsMonitor.CurrentValue;
            if (string.IsNullOrWhiteSpace(emailOptions.Username) ||
                string.IsNullOrWhiteSpace(emailOptions.AppPassword) ||
                string.IsNullOrWhiteSpace(emailOptions.ToAddress))
            {
                _logger.LogWarning("DailySummary is enabled but Email settings are incomplete; scheduled digests will not be sent.");
                if (!await WaitAsync(TimeSpan.FromSeconds(60), stoppingToken)) break;
                continue;
            }

            var (fireAt, kind) = GetNextRun(DateTimeOffset.Now, options.MorningHour, options.EveningHour);
            var delay = fireAt - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero && !await WaitAsync(delay, stoppingToken)) break;
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await SendDigestAsync(kind, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send the {Kind} summary email", kind);
            }

            // Guards against re-firing immediately in the same minute the delay was computed in.
            if (!await WaitAsync(TimeSpan.FromMinutes(1), stoppingToken)) break;
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

    public static (DateTimeOffset FireAt, DigestKind Kind) GetNextRun(DateTimeOffset now, int morningHour, int eveningHour)
    {
        var today = new DateTimeOffset(now.Date, now.Offset);
        var candidates = new[]
        {
            (FireAt: today.AddHours(morningHour), Kind: DigestKind.Morning),
            (FireAt: today.AddHours(eveningHour), Kind: DigestKind.Evening)
        };

        var upcomingToday = candidates.Where(c => c.FireAt > now).OrderBy(c => c.FireAt).ToList();
        if (upcomingToday.Count > 0) return upcomingToday[0];

        var earliestTomorrow = candidates.OrderBy(c => c.FireAt).First();
        return (earliestTomorrow.FireAt.AddDays(1), earliestTomorrow.Kind);
    }

    private async Task SendDigestAsync(DigestKind kind, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var emailOptions = _emailOptionsMonitor.CurrentValue;
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);

        var (from, to, title) = kind == DigestKind.Evening
            ? (today.AddHours(options.MorningHour), now, "Today's activity summary")
            : (today.AddHours(options.EveningHour).AddDays(-1), now, "Overnight activity summary");

        var events = await _eventStore.GetEventsAsync(from, to, cancellationToken);
        var analyzed = events.Where(e => string.Equals(e.Status, "completed", StringComparison.OrdinalIgnoreCase)).ToList();

        using var message = await BuildMessageAsync(title, from, to, analyzed, cancellationToken);
        using var client = new SmtpClient(emailOptions.SmtpHost, emailOptions.SmtpPort)
        {
            EnableSsl = emailOptions.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(emailOptions.Username, emailOptions.AppPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        await client.SendMailAsync(message).WaitAsync(cancellationToken);

        _logger.LogInformation(
            "Sent {Kind} summary email covering {From} to {To}: {Count} analyzed event(s)",
            kind, from, to, analyzed.Count);
    }

    public async Task<MailMessage> BuildMessageAsync(
        string title,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<AlertEventRecord> events,
        CancellationToken cancellationToken)
    {
        var emailOptions = _emailOptionsMonitor.CurrentValue;
        var fromAddress = string.IsNullOrWhiteSpace(emailOptions.FromAddress) ? emailOptions.Username : emailOptions.FromAddress;
        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, emailOptions.FromName),
            Subject = $"{title}: {events.Count} event{(events.Count == 1 ? "" : "s")}",
            IsBodyHtml = true
        };
        message.To.Add(emailOptions.ToAddress);

        var notable = events.Where(e => e.NotificationRecommended == true).ToList();
        var thumbnails = await LoadThumbnailsAsync(notable, cancellationToken);
        var html = BuildHtml(title, from, to, events, notable, thumbnails);

        var view = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html);
        foreach (var (cid, bytes, _) in thumbnails)
        {
            var resource = new LinkedResource(new MemoryStream(bytes), MediaTypeNames.Image.Jpeg)
            {
                ContentId = cid,
                TransferEncoding = TransferEncoding.Base64
            };
            view.LinkedResources.Add(resource);
        }
        message.AlternateViews.Add(view);

        return message;
    }

    private async Task<List<(string Cid, byte[] Bytes, AlertEventRecord Event)>> LoadThumbnailsAsync(
        IReadOnlyList<AlertEventRecord> notable,
        CancellationToken cancellationToken)
    {
        var thumbnails = new List<(string, byte[], AlertEventRecord)>();
        var index = 0;
        foreach (var evt in notable.Take(_optionsMonitor.CurrentValue.MaxImagesAttached))
        {
            if (string.IsNullOrWhiteSpace(evt.ImagePath) || !File.Exists(evt.ImagePath)) continue;

            try
            {
                var sourceBytes = await File.ReadAllBytesAsync(evt.ImagePath, cancellationToken);
                var optimized = await _imageOptimizer.OptimizeForInferenceAsync(sourceBytes, crop: null, cancellationToken);
                thumbnails.Add(($"digest-image-{index++}", optimized.Bytes, evt));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping digest thumbnail for alert {AlertId}", evt.Id);
            }
        }

        return thumbnails;
    }

    public static string BuildHtml(
        string title,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<AlertEventRecord> events,
        IReadOnlyList<AlertEventRecord> notable,
        IReadOnlyList<(string Cid, byte[] Bytes, AlertEventRecord Event)> thumbnails)
    {
        var galleryHtml = new StringBuilder();
        foreach (var (cid, _, evt) in thumbnails)
        {
            galleryHtml.Append($$"""
                <div style="display:inline-block;margin:0 8px 12px 0;text-align:center;">
                  <img src="cid:{{cid}}" alt="{{WebUtility.HtmlEncode(evt.Summary)}}" style="max-width:220px;height:auto;border:1px solid #ccc;" />
                  <div style="font-size:12px;color:#555;max-width:220px;">{{WebUtility.HtmlEncode(evt.OccurredAt.ToString("h:mm tt"))}} - {{WebUtility.HtmlEncode(evt.Camera)}}</div>
                </div>
                """);
        }

        var rows = new StringBuilder();
        foreach (var evt in events)
        {
            rows.Append($$"""
                <tr>
                  <td style="padding:4px 12px 4px 0;white-space:nowrap;">{{WebUtility.HtmlEncode(evt.OccurredAt.ToString("h:mm tt"))}}</td>
                  <td style="padding:4px 12px 4px 0;">{{WebUtility.HtmlEncode(evt.Camera)}}</td>
                  <td style="padding:4px 12px 4px 0;">{{WebUtility.HtmlEncode(evt.EventType ?? "unknown")}}</td>
                  <td style="padding:4px 12px 4px 0;">{{WebUtility.HtmlEncode(evt.Summary ?? string.Empty)}}</td>
                  <td style="padding:4px 12px 4px 0;">{{(evt.NotificationRecommended == true ? "Yes" : "No")}}</td>
                </tr>
                """);
        }

        var bodyTable = events.Count == 0
            ? """<p style="color:#555;">No camera activity was analyzed in this window.</p>"""
            : $$"""
              <table style="border-collapse:collapse;font-size:14px;">
                <tr style="text-align:left;border-bottom:1px solid #ccc;">
                  <th style="padding:4px 12px 4px 0;">Time</th>
                  <th style="padding:4px 12px 4px 0;">Camera</th>
                  <th style="padding:4px 12px 4px 0;">Event</th>
                  <th style="padding:4px 12px 4px 0;">Summary</th>
                  <th style="padding:4px 12px 4px 0;">Notified</th>
                </tr>
                {{rows}}
              </table>
              """;

        return $$"""
        <!doctype html>
        <html>
        <body style="font-family:Arial,Helvetica,sans-serif;color:#222;">
          <h2>{{WebUtility.HtmlEncode(title)}}</h2>
          <p style="font-size:13px;color:#555;">
            {{WebUtility.HtmlEncode(from.ToString("f"))}} to {{WebUtility.HtmlEncode(to.ToString("f"))}}
            &mdash; {{events.Count}} analyzed event{{(events.Count == 1 ? "" : "s")}}, {{notable.Count}} notification-worthy
          </p>
          {{(galleryHtml.Length > 0 ? $"<div>{galleryHtml}</div>" : string.Empty)}}
          {{bodyTable}}
        </body>
        </html>
        """;
    }
}
