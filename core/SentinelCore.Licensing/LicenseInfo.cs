namespace SentinelCore.Licensing;

// Deliberately minimal - a one-time-purchase, perpetual license with no expiry and no machine
// binding. The signature is what makes this trustworthy (nobody without the private key can
// forge or edit one), not any enforcement built on top of it.
public sealed record LicenseInfo(
    string CustomerName,
    string CustomerEmail,
    string Product,
    Guid LicenseId,
    DateTimeOffset IssuedAt);
