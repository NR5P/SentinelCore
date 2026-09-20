namespace SentinelCore;

// The one thing a push says, independent of which provider carries it. Built from an AlertResult
// by PushMessages.FromAlert in the main app; the relay receives it as JSON and hands it straight
// to the same senders, so its shape is the relay's wire format too.
public sealed record PushMessage(
    string Title,
    string Body,
    string? EventId,
    string? Camera,
    string? EventType)
{
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
