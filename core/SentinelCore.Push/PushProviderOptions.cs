namespace SentinelCore;

public sealed class ApplePushOptions
{
    public bool Enabled { get; init; }
    public string TeamId { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string BundleId { get; init; } = "com.sentinelcore.client";
    // Apple requires the apns-topic to exactly match the receiving app's own bundle id, so the
    // tvOS app (a separate App ID) needs its own topic here rather than reusing BundleId. Left
    // blank, ApnsSender falls back to BundleId - that push will simply be rejected by APNs for
    // tvOS devices rather than crash anything.
    public string TvBundleId { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public bool UseSandbox { get; init; } = true;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(TeamId) &&
        !string.IsNullOrWhiteSpace(KeyId) &&
        !string.IsNullOrWhiteSpace(BundleId) &&
        !string.IsNullOrWhiteSpace(PrivateKeyPath);

    // Pulled out as a pure function so the topic-selection rule (tvOS gets its own topic,
    // everything else keeps using BundleId) is directly testable without standing up ECDSA
    // signing or a fake HTTP pipeline.
    public string ResolveApnsTopic(string appVariant) =>
        string.Equals(appVariant, "tvos", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(TvBundleId)
            ? TvBundleId
            : BundleId;
}

public sealed class FirebasePushOptions
{
    public bool Enabled { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string ServiceAccountPath { get; init; } = string.Empty;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ProjectId) &&
        !string.IsNullOrWhiteSpace(ServiceAccountPath);
}
