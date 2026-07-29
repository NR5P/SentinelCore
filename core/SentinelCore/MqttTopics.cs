using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore;

// Pure topic-string and payload builders, kept separate from anything that touches an actual
// MQTT connection so the exact topic shapes and Home Assistant discovery JSON are easy to test
// directly without a broker.
public static class MqttTopics
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Availability(string topicPrefix) => $"{topicPrefix}/status";

    public static string EventState(string topicPrefix, string camera) => $"{topicPrefix}/{Slug(camera)}/event/state";

    public static string EventAttributes(string topicPrefix, string camera) => $"{topicPrefix}/{Slug(camera)}/event/attributes";

    public static string NotifiedState(string topicPrefix, string camera) => $"{topicPrefix}/{Slug(camera)}/notified/state";

    public static string DeviceId(string topicPrefix, string camera) => $"{Slug(topicPrefix)}_{Slug(camera)}";

    public static string DiscoveryConfigTopic(string discoveryPrefix, string component, string topicPrefix, string camera, string objectId) =>
        $"{discoveryPrefix}/{component}/{DeviceId(topicPrefix, camera)}/{objectId}/config";

    // MQTT topic segments and Home Assistant object_ids only allow a narrow, predictable
    // character set safely - this keeps a camera name with spaces or punctuation (as Blue Iris
    // or a user-entered camera name might have) from producing an invalid or surprising topic.
    public static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

    private static object DeviceBlock(string topicPrefix, string camera) => new
    {
        identifiers = new[] { DeviceId(topicPrefix, camera) },
        name = $"{camera} (Sentinel Core)",
        manufacturer = "Sentinel Core",
        model = "Camera"
    };

    public static string BuildNotifiedDiscoveryPayload(MqttOptions options, string camera)
    {
        var payload = new
        {
            name = "Notified",
            unique_id = $"{DeviceId(options.TopicPrefix, camera)}_notified",
            state_topic = NotifiedState(options.TopicPrefix, camera),
            payload_on = "ON",
            payload_off = "OFF",
            off_delay = 30,
            device_class = "occupancy",
            availability_topic = Availability(options.TopicPrefix),
            payload_available = "online",
            payload_not_available = "offline",
            device = DeviceBlock(options.TopicPrefix, camera)
        };
        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }

    public static string BuildLastEventDiscoveryPayload(MqttOptions options, string camera)
    {
        var payload = new
        {
            name = "Last Event",
            unique_id = $"{DeviceId(options.TopicPrefix, camera)}_last_event",
            state_topic = EventState(options.TopicPrefix, camera),
            json_attributes_topic = EventAttributes(options.TopicPrefix, camera),
            icon = "mdi:cctv",
            availability_topic = Availability(options.TopicPrefix),
            payload_available = "online",
            payload_not_available = "offline",
            device = DeviceBlock(options.TopicPrefix, camera)
        };
        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }

    public static string BuildEventAttributesPayload(AlertResult result)
    {
        var analysis = result.Analysis;
        var payload = new
        {
            camera = result.Camera,
            occurred_at = result.OccurredAt,
            profile = result.Profile,
            summary = analysis?.Summary,
            confidence = analysis?.Confidence,
            notification_recommended = analysis?.NotificationRecommended,
            notification_reason = analysis?.NotificationReason,
            person_visible = analysis?.Person.Visible,
            person_known_face_labels = analysis?.Person.KnownFaceLabels,
            person_unrecognized_face_detected = analysis?.Person.UnrecognizedFaceDetected,
            vehicle_visible = analysis?.Vehicle.Visible,
            vehicle_license_plate = string.IsNullOrWhiteSpace(analysis?.Vehicle.LicensePlateText) ? null : analysis.Vehicle.LicensePlateText,
            vehicle_known_label = analysis?.Vehicle.KnownVehicleLabel,
            package_status = analysis?.Package.Status,
            animal_visible = analysis?.Animal.Visible
        };
        return JsonSerializer.Serialize(payload, PayloadJsonOptions);
    }
}
