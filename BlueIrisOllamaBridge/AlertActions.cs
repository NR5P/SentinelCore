using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

public interface IAlertAction
{
    Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken);
}

public sealed class WebhookAlertAction : IAlertAction
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<WebhookOptions> _optionsMonitor;
    private readonly ILogger<WebhookAlertAction> _logger;

    public WebhookAlertAction(HttpClient httpClient, IOptionsMonitor<WebhookOptions> options, ILogger<WebhookAlertAction> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var _options = _optionsMonitor.CurrentValue;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Url)) return;
        if (_options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url)
        {
            Content = JsonContent.Create(result)
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Webhook returned {StatusCode}: {Body}", (int)response.StatusCode, body);
        }
    }
}

// What kind of thing an alert is about, shared by email and Telegram so a stationary vehicle
// or a person lingering by the gate maps to the same category in both places - that's what
// each channel's duplicate-cooldown keys off of, so "the same vehicle is still there" gets
// suppressed instead of re-sent every time a new frame happens to trigger analysis.
public static class AlertCategorizer
{
    public static string? Categorize(VisionAnalysis analysis)
    {
        if (EqualsValue(analysis.Package.Status, "delivered"))
            return "package-delivered";

        if (EqualsValue(analysis.EventType, "delivery_in_progress") ||
            EqualsValue(analysis.Package.Status, "being_delivered") ||
            EqualsValue(analysis.Person.Activity, "placing_package"))
            return "delivery-in-progress";

        if (analysis.Person.OutsideVehicle &&
            (EqualsValue(analysis.Vehicle.Location, "outside_gate_entrance") ||
             EqualsValue(analysis.Person.Location, "at_gate") ||
             EqualsValue(analysis.Person.Location, "near_vehicle")))
            return "person-outside-vehicle";

        if (analysis.Person.Visible && EqualsValue(analysis.Person.Activity, "walking") && IsNearGate(analysis.Person.Location))
            return "person-walking";

        if (analysis.Person.Visible && IsNearGate(analysis.Person.Location))
            return "person-at-gate";

        if (EqualsValue(analysis.Vehicle.Activity, "entering") || EqualsValue(analysis.EventType, "vehicle_entering_property"))
            return "vehicle-entering";

        if (analysis.Vehicle.Visible && EqualsValue(analysis.Vehicle.Location, "outside_gate_entrance") &&
            (EqualsValue(analysis.Vehicle.Activity, "stopped") || EqualsValue(analysis.Vehicle.Activity, "unknown")))
            return "vehicle-at-gate";

        if (analysis.Vehicle.Visible && EqualsValue(analysis.Vehicle.Location, "inside_driveway"))
            return "vehicle-inside";

        if (analysis.Person.Visible && EqualsValue(analysis.Person.Location, "inside_property"))
            return "person-on-property";

        if (analysis.Animal.Visible)
            return "animal";

        return null;
    }

    private static bool IsNearGate(string location) =>
        EqualsValue(location, "outside_gate") ||
        EqualsValue(location, "at_gate") ||
        EqualsValue(location, "near_vehicle");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class TelegramAlertAction : IAlertAction
{
    private const string ApiBaseUrl = "https://api.telegram.org";

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<TelegramOptions> _optionsMonitor;
    private readonly ILogger<TelegramAlertAction> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    public TelegramAlertAction(HttpClient httpClient, IOptionsMonitor<TelegramOptions> options, ILogger<TelegramAlertAction> logger)
    {
        _httpClient = httpClient;
        _optionsMonitor = options;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.BotToken) || string.IsNullOrWhiteSpace(options.ChatId))
            return;
        if (options.OnlyWhenNotificationRecommended && !AlertNotificationPolicy.ShouldNotify(result))
            return;
        if (result.Analysis is not null && result.Analysis.Confidence < options.MinimumConfidence)
            return;

        // Only gate on category when one can be determined - anything that doesn't map to a
        // known category still sends every time, same as before this cooldown existed.
        string? cooldownKey = null;
        if (result.Analysis is not null && AlertCategorizer.Categorize(result.Analysis) is { } category)
        {
            cooldownKey = $"{result.Camera}|{category}";
            if (IsInCooldown(options, cooldownKey))
            {
                _logger.LogInformation(
                    "Duplicate Telegram category {Category} suppressed for camera {Camera}",
                    category, result.Camera);
                return;
            }
        }

        var caption = BuildCaption(result);
        var imageExists = options.AttachImage && !string.IsNullOrWhiteSpace(result.ImagePath) && File.Exists(result.ImagePath);

        using var response = imageExists
            ? await SendPhotoAsync(options, caption, result.ImagePath!, cancellationToken)
            : await SendMessageAsync(options, caption, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Telegram send failed for {AlertId}: {StatusCode} {Body}", result.Id, (int)response.StatusCode, body);
        }
        else
        {
            if (cooldownKey is not null) _lastSent[cooldownKey] = DateTimeOffset.UtcNow;
            _logger.LogInformation("Telegram alert sent for {AlertId}", result.Id);
        }
    }

    private async Task<HttpResponseMessage> SendMessageAsync(TelegramOptions options, string caption, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>
        {
            ["chat_id"] = options.ChatId,
            ["text"] = caption,
            ["parse_mode"] = "HTML"
        };
        return await _httpClient.PostAsync(
            $"{ApiBaseUrl}/bot{options.BotToken}/sendMessage",
            new FormUrlEncodedContent(payload),
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendPhotoAsync(TelegramOptions options, string caption, string imagePath, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(options.ChatId), "chat_id" },
            { new StringContent(caption), "caption" },
            { new StringContent("HTML"), "parse_mode" }
        };

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(imageContent, "photo", Path.GetFileName(imagePath));

        return await _httpClient.PostAsync($"{ApiBaseUrl}/bot{options.BotToken}/sendPhoto", content, cancellationToken);
    }

    public static string BuildCaption(AlertResult result)
    {
        var analysis = result.Analysis;
        if (analysis is null) return $"Alert from {WebUtility.HtmlEncode(result.Camera)}";

        var caption = new StringBuilder();
        caption.Append($"<b>{WebUtility.HtmlEncode(result.Camera)}</b>\n");
        caption.Append(WebUtility.HtmlEncode(analysis.Summary));
        if (!string.IsNullOrWhiteSpace(analysis.NotificationReason))
            caption.Append($"\n{WebUtility.HtmlEncode(analysis.NotificationReason)}");

        return caption.ToString();
    }

    private bool IsInCooldown(TelegramOptions options, string key)
    {
        if (options.DuplicateCooldownMinutes <= 0) return false;
        if (!_lastSent.TryGetValue(key, out var lastSent)) return false;
        return DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(options.DuplicateCooldownMinutes);
    }
}

public sealed class EmailAlertAction : IAlertAction
{
    private readonly IOptionsMonitor<EmailOptions> _optionsMonitor;
    private readonly IPromptProfileProvider _profileProvider;
    private readonly IImageOptimizer _imageOptimizer;
    private readonly ILogger<EmailAlertAction> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    public EmailAlertAction(
        IOptionsMonitor<EmailOptions> options,
        IPromptProfileProvider profileProvider,
        IImageOptimizer imageOptimizer,
        ILogger<EmailAlertAction> logger)
    {
        _optionsMonitor = options;
        _profileProvider = profileProvider;
        _imageOptimizer = imageOptimizer;
        _logger = logger;
    }

    public async Task ExecuteAsync(AlertResult result, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled || result.Analysis is null) return;

        var analysis = result.Analysis;
        if (!AlertNotificationPolicy.ShouldNotify(result) || analysis.Confidence < options.MinimumConfidence)
        {
            _logger.LogInformation(
                "Email skipped for {AlertId}; recommended={Recommended}, confidence={Confidence}",
                result.Id,
                analysis.NotificationRecommended,
                analysis.Confidence);
            return;
        }

        var notification = BuildNotification(analysis);
        if (notification is null) return;

        var cooldownKey = $"{result.Camera}|{notification.Category}";
        if (IsInCooldown(options, cooldownKey))
        {
            _logger.LogInformation(
                "Duplicate email category {Category} suppressed for camera {Camera}",
                notification.Category,
                result.Camera);
            return;
        }

        var cropOverlay = await TryBuildCropOverlayAsync(result, cancellationToken);

        using var message = BuildMessage(options, result, notification, cropOverlay);
        using var client = new SmtpClient(options.SmtpHost, options.SmtpPort)
        {
            EnableSsl = options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(options.Username, options.AppPassword),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        await client.SendMailAsync(message).WaitAsync(cancellationToken);
        _lastSent[cooldownKey] = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Email alert sent for {AlertId} with subject {Subject}",
            result.Id,
            notification.Subject);
    }

    private bool IsInCooldown(EmailOptions options, string key)
    {
        if (options.DuplicateCooldownMinutes <= 0) return false;
        if (!_lastSent.TryGetValue(key, out var lastSent)) return false;
        return DateTimeOffset.UtcNow - lastSent < TimeSpan.FromMinutes(options.DuplicateCooldownMinutes);
    }

    private async Task<byte[]?> TryBuildCropOverlayAsync(AlertResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.ImagePath) || !File.Exists(result.ImagePath))
            return null;
        if (!_profileProvider.Current.TryGetValue(result.Profile, out var profile) || profile.Crop is null)
            return null;

        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(result.ImagePath, cancellationToken);
            return await _imageOptimizer.DrawCropOverlayAsync(sourceBytes, profile.Crop, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build the analyzed-region image for {AlertId}", result.Id);
            return null;
        }
    }

    private MailMessage BuildMessage(EmailOptions options, AlertResult result, EmailNotification notification, byte[]? cropOverlay)
    {
        var fromAddress = string.IsNullOrWhiteSpace(options.FromAddress)
            ? options.Username
            : options.FromAddress;

        var message = new MailMessage
        {
            From = new MailAddress(fromAddress, options.FromName),
            Subject = notification.Subject,
            IsBodyHtml = true
        };
        message.To.Add(options.ToAddress);

        var analysis = result.Analysis!;
        var imageExists = !string.IsNullOrWhiteSpace(result.ImagePath) && File.Exists(result.ImagePath);
        var imageHtml = imageExists
            ? "<p><img src=\"cid:gate-alert-image\" alt=\"Gate camera alert\" style=\"max-width:100%;height:auto;border:1px solid #ccc;\" /></p>"
            : string.Empty;
        var cropHtml = cropOverlay is not null
            ? """
              <p style="font-size:13px;color:#555;margin-bottom:2px;">Area the AI analyzed (outlined in red):</p>
              <p><img src="cid:gate-alert-crop" alt="Analyzed region" style="max-width:100%;height:auto;border:1px solid #ccc;" /></p>
              """
            : string.Empty;

        var html = $$"""
        <!doctype html>
        <html>
        <body style="font-family:Arial,Helvetica,sans-serif;color:#222;">
          <h2>{{WebUtility.HtmlEncode(notification.Subject)}}</h2>
          <p style="font-size:16px;">{{WebUtility.HtmlEncode(analysis.Summary)}}</p>
          {{imageHtml}}
          {{cropHtml}}
          <table style="border-collapse:collapse;">
            <tr><td style="padding:4px 12px 4px 0;"><strong>Camera</strong></td><td>{{WebUtility.HtmlEncode(result.Camera)}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Time</strong></td><td>{{WebUtility.HtmlEncode(result.OccurredAt.ToLocalTime().ToString("f"))}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Event</strong></td><td>{{WebUtility.HtmlEncode(analysis.EventType)}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Vehicle</strong></td><td>{{WebUtility.HtmlEncode(BuildVehicleText(analysis.Vehicle))}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Person</strong></td><td>{{WebUtility.HtmlEncode(BuildPersonText(analysis.Person))}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Package</strong></td><td>{{WebUtility.HtmlEncode(BuildPackageText(analysis.Package))}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Confidence</strong></td><td>{{analysis.Confidence:P0}}</td></tr>
            <tr><td style="padding:4px 12px 4px 0;"><strong>Reason</strong></td><td>{{WebUtility.HtmlEncode(analysis.NotificationReason)}}</td></tr>
          </table>
        </body>
        </html>
        """;

        var view = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html);
        if (imageExists)
        {
            var resource = new LinkedResource(result.ImagePath!)
            {
                ContentId = "gate-alert-image",
                TransferEncoding = TransferEncoding.Base64
            };
            resource.ContentType.Name = Path.GetFileName(result.ImagePath);
            view.LinkedResources.Add(resource);
        }
        if (cropOverlay is not null)
        {
            var cropResource = new LinkedResource(new MemoryStream(cropOverlay), MediaTypeNames.Image.Jpeg)
            {
                ContentId = "gate-alert-crop",
                TransferEncoding = TransferEncoding.Base64
            };
            cropResource.ContentType.Name = "analyzed-region.jpg";
            view.LinkedResources.Add(cropResource);
        }
        message.AlternateViews.Add(view);

        if (options.AttachImage && imageExists)
            message.Attachments.Add(new Attachment(result.ImagePath!));
        if (options.AttachImage && cropOverlay is not null)
            message.Attachments.Add(new Attachment(new MemoryStream(cropOverlay), "analyzed-region.jpg", MediaTypeNames.Image.Jpeg));

        return message;
    }

    private static EmailNotification? BuildNotification(VisionAnalysis analysis)
    {
        if (EqualsValue(analysis.Package.Status, "delivered"))
            return new("package-delivered", "Package is here at your gate");

        if (EqualsValue(analysis.EventType, "delivery_in_progress") ||
            EqualsValue(analysis.Package.Status, "being_delivered") ||
            EqualsValue(analysis.Person.Activity, "placing_package"))
        {
            var company = FormatDeliveryCompany(analysis.Vehicle);
            var subject = company is null
                ? "Delivery driver at your gate"
                : $"{company} delivery driver at your gate";
            return new("delivery-in-progress", subject);
        }

        if (analysis.Person.OutsideVehicle &&
            (EqualsValue(analysis.Vehicle.Location, "outside_gate_entrance") ||
             EqualsValue(analysis.Person.Location, "at_gate") ||
             EqualsValue(analysis.Person.Location, "near_vehicle")))
        {
            return new("person-outside-vehicle", "Person is outside a vehicle in front of your gate");
        }

        if (analysis.Person.Visible && EqualsValue(analysis.Person.Activity, "walking") && IsNearGate(analysis.Person.Location))
            return new("person-walking", "Person is walking around near your gate");

        if (analysis.Person.Visible && IsNearGate(analysis.Person.Location))
            return new("person-at-gate", "Person is standing by your gate");

        if (EqualsValue(analysis.Vehicle.Activity, "entering") ||
            EqualsValue(analysis.EventType, "vehicle_entering_property"))
        {
            return new("vehicle-entering", $"Vehicle is entering your property: {VehicleDescription(analysis.Vehicle)}");
        }

        if (analysis.Vehicle.Visible &&
            EqualsValue(analysis.Vehicle.Location, "outside_gate_entrance") &&
            (EqualsValue(analysis.Vehicle.Activity, "stopped") || EqualsValue(analysis.Vehicle.Activity, "unknown")))
        {
            return new("vehicle-at-gate", $"Vehicle is here at your gate: {VehicleDescription(analysis.Vehicle)}");
        }

        if (analysis.Vehicle.Visible && EqualsValue(analysis.Vehicle.Location, "inside_driveway"))
        {
            return new("vehicle-inside", $"Vehicle is in your driveway: {VehicleDescription(analysis.Vehicle)}");
        }

        if (analysis.Person.Visible && EqualsValue(analysis.Person.Location, "inside_property"))
            return new("person-on-property", "Person detected inside your property");

        if (analysis.Animal.Visible)
            return new("animal", "Animal detected near your gate");

        return null;
    }

    private static string? FormatDeliveryCompany(VehicleAnalysis vehicle)
    {
        if (vehicle.DeliveryCompanyConfidence < 0.70) return null;
        return vehicle.DeliveryCompany.ToLowerInvariant() switch
        {
            "ups" => "UPS",
            "fedex" => "FedEx",
            "usps" => "USPS",
            "amazon" => "Amazon",
            "dhl" => "DHL",
            _ => null
        };
    }

    private static string VehicleDescription(VehicleAnalysis vehicle)
    {
        if (!string.IsNullOrWhiteSpace(vehicle.Description)) return vehicle.Description;

        var parts = new List<string>();
        if (!EqualsValue(vehicle.Color, "unknown") && !EqualsValue(vehicle.Color, "none"))
            parts.Add(vehicle.Color);
        if (!EqualsValue(vehicle.Type, "unknown") && !EqualsValue(vehicle.Type, "none"))
            parts.Add(vehicle.Type.Replace('_', ' '));

        return parts.Count > 0 ? string.Join(' ', parts) : "unknown vehicle";
    }

    private static string BuildVehicleText(VehicleAnalysis vehicle) =>
        vehicle.Visible
            ? $"{VehicleDescription(vehicle)}; location={vehicle.Location}; activity={vehicle.Activity}"
            : "Not visible";

    private static string BuildPersonText(PersonAnalysis person) =>
        person.Visible
            ? $"{(string.IsNullOrWhiteSpace(person.Description) ? "Person" : person.Description)}; location={person.Location}; activity={person.Activity}"
            : "Not visible";

    private static string BuildPackageText(PackageAnalysis package) =>
        package.Visible || !EqualsValue(package.Status, "none")
            ? $"status={package.Status}; location={package.Location}"
            : "Not visible";

    private static bool IsNearGate(string location) =>
        EqualsValue(location, "outside_gate") ||
        EqualsValue(location, "at_gate") ||
        EqualsValue(location, "near_vehicle");

    private static bool EqualsValue(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private sealed record EmailNotification(string Category, string Subject);
}
