namespace SentinelCore;

// The one thing a push says, independent of which provider carries it. Built from an AlertResult
// by PushMessages.FromAlert in the main app; the relay receives it as JSON and hands it straight
// to the same senders, so its shape is the relay's wire format too.
//
// A Silent message shows nothing: it wakes the app in the background to fetch something (an APNs
// background push, an FCM data-only message), with EventType naming what. The relay predates
// these and would turn one into a blank alert, so PushNotificationDispatcher never relays them.
public sealed record PushMessage(
    string Title,
    string Body,
    string? EventId,
    string? Camera,
    string? EventType,
    bool Silent = false,
    // An Organizer item to open when the notification is tapped (a board automation's card).
    // The apps already open the Organizer for "organizerItemId", which their own reminder alerts
    // carry; older relays ignore the property.
    string? OrganizerItemId = null,
    // A generic notification for another product the relay carries (ReelHouse). Non-null Data
    // switches both senders from Sentinel's security payload to a plain alert whose Data entries
    // are handed to the app as-is; AndroidChannel and ThreadId pick the Android notification
    // channel and the APNs thread / FCM tag. Sentinel Core never sets these, and older relays
    // ignore the properties.
    IReadOnlyDictionary<string, string>? Data = null,
    string? AndroidChannel = null,
    string? ThreadId = null)
{
    public const string OrganizerType = "organizer";

    // The home alarm: triggered, armed, disarmed, a sensor's battery. Time-sensitive on iOS so it
    // breaks through a Focus; the apps open the Alarm tab for it.
    public const string AlarmType = "alarm";

    public const string OrganizerChangedType = "organizer-changed";

    // Something in the Organizer changed: devices sync in the background so their Home Screen
    // widgets and offline copy are current without the app being opened.
    public static PushMessage OrganizerChanged() => new("", "", null, null, OrganizerChangedType, Silent: true);

    public static PushMessage Test() => new(
        "Sentinel Core test",
        "Push notifications are connected and ready.",
        null,
        null,
        "test");
}

public enum PushAttempt
{
    Skipped,
    Delivered,
    Failed,
    // The provider says this token will never work again (APNs 410 / FCM UNREGISTERED). The
    // caller owns the device list, so it is the caller that drops the registration.
    Unregistered
}
