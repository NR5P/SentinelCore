namespace SentinelCore;

// The two platform names as they appear in device registrations, on both sides of the relay.
public static class PushPlatforms
{
    public const string Apple = "apple";
    public const string Android = "android";

    public static bool IsSupported(string value) =>
        string.Equals(value, Apple, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Android, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
