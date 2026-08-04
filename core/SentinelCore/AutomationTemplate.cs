using System.Text;
using System.Text.RegularExpressions;

namespace SentinelCore;

// Tiny {{token}} substitution shared by an automation rule's MQTT topic/payload and webhook
// URL/headers/body (see AutomationAlertAction). Deliberately not a general templating engine -
// just enough to drop values from the analysis into a topic string or a JSON body.
public static partial class AutomationTemplate
{
    // Kept in sync by hand with BuildValues below - exposed so the dashboard can show a legend of
    // what's available while editing a rule (see AutomationRuleEndpoints's /metadata route).
    public static readonly IReadOnlyList<string> KnownTokens =
    [
        "camera", "profile", "alert_id", "occurred_at", "event_type", "category", "summary",
        "confidence", "confidence_percent", "notification_reason", "notification_recommended",
        "person_visible", "person_location", "person_activity", "person_description",
        "person_known_face_labels", "person_unrecognized_face_detected",
        "vehicle_visible", "vehicle_location", "vehicle_activity", "vehicle_type", "vehicle_color",
        "vehicle_description", "vehicle_license_plate", "vehicle_known_label", "vehicle_delivery_company",
        "package_status", "package_location", "animal_visible", "animal_description"
    ];

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex TokenPattern();

    // Tokens a schedule can use. A scheduled firing has no camera and no analysis behind it, so
    // it gets its own much smaller set - see BuildScheduleValues.
    public static readonly IReadOnlyList<string> ScheduleTokens =
    [
        "schedule_name", "schedule_kind", "run_id", "now", "now_utc", "unix", "due_at"
    ];

    public static string Render(string template, AlertResult result, string? category) =>
        Render(template, BuildValues(result, category));

    // The substitution itself, over whatever values the caller has. Everything above and below
    // is just a different way of filling that dictionary.
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template)) return template;

        return TokenPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value.ToLowerInvariant();
            return values.TryGetValue(key, out var value) ? EscapeForTemplate(value) : match.Value;
        });
    }

    public static Dictionary<string, string> BuildScheduleValues(
        Schedule schedule, Guid runId, DateTimeOffset dueAt, DateTimeOffset firedAt) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["schedule_name"] = schedule.Name,
            ["schedule_kind"] = schedule.Kind,
            ["run_id"] = runId.ToString(),
            ["now"] = firedAt.ToLocalTime().ToString("O"),
            ["now_utc"] = firedAt.ToUniversalTime().ToString("O"),
            ["unix"] = firedAt.ToUnixTimeSeconds().ToString(),
            ["due_at"] = dueAt.ToLocalTime().ToString("O")
        };

    public static Dictionary<string, string> BuildValues(AlertResult result, string? category)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["camera"] = result.Camera,
            ["profile"] = result.Profile,
            ["alert_id"] = result.Id.ToString(),
            ["occurred_at"] = result.OccurredAt.ToUniversalTime().ToString("O"),
            ["category"] = category ?? "none"
        };

        var analysis = result.Analysis;
        if (analysis is null) return values;

        values["event_type"] = analysis.EventType;
        values["summary"] = analysis.Summary;
        values["confidence"] = analysis.Confidence.ToString("0.00");
        values["confidence_percent"] = ((int)Math.Round(analysis.Confidence * 100)).ToString();
        values["notification_reason"] = analysis.NotificationReason;
        values["notification_recommended"] = analysis.NotificationRecommended ? "true" : "false";
        values["person_visible"] = analysis.Person.Visible ? "true" : "false";
        values["person_location"] = analysis.Person.Location;
        values["person_activity"] = analysis.Person.Activity;
        values["person_description"] = analysis.Person.Description;
        values["person_known_face_labels"] = string.Join(", ", analysis.Person.KnownFaceLabels);
        values["person_unrecognized_face_detected"] = analysis.Person.UnrecognizedFaceDetected ? "true" : "false";
        values["vehicle_visible"] = analysis.Vehicle.Visible ? "true" : "false";
        values["vehicle_location"] = analysis.Vehicle.Location;
        values["vehicle_activity"] = analysis.Vehicle.Activity;
        values["vehicle_type"] = analysis.Vehicle.Type;
        values["vehicle_color"] = analysis.Vehicle.Color;
        values["vehicle_description"] = analysis.Vehicle.Description;
        values["vehicle_license_plate"] = analysis.Vehicle.LicensePlateText;
        values["vehicle_known_label"] = analysis.Vehicle.KnownVehicleLabel ?? "";
        values["vehicle_delivery_company"] = analysis.Vehicle.DeliveryCompany;
        values["package_status"] = analysis.Package.Status;
        values["package_location"] = analysis.Package.Location;
        values["animal_visible"] = analysis.Animal.Visible ? "true" : "false";
        values["animal_description"] = analysis.Animal.Description;

        return values;
    }

    // Escapes the same characters System.Text.Json would inside a string literal - enough to keep
    // AI-generated free text (summary, descriptions) from breaking a JSON payload template if it
    // happens to contain a quote, backslash, or newline. Harmless in a plain-text MQTT topic/payload
    // too, since none of these values normally contain the characters escaping changes.
    private static string EscapeForTemplate(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }
}
