using System.ComponentModel.DataAnnotations;
using SentinelCore.Licensing;

namespace SentinelCore.PushRelay;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    // The same option shapes the main app uses for its own direct mode, so the values are copied
    // straight across from a working Core install. Apple.UseSandbox is ignored here: the
    // environment is per registered token, not per relay.
    public ApplePushOptions Apple { get; init; } = new();
    public FirebasePushOptions Firebase { get; init; } = new();

    [Required]
    public string DataDirectory { get; init; } = "data";

    // Per license. A household is a handful of phones and an Apple TV or two; a number in the
    // hundreds means a license file is being shared around, and the cap is what makes that show
    // up as a refusal in the customer's log rather than as a bill.
    [Range(1, 1000)]
    public int MaxDevicesPerLicense { get; init; } = 25;

    // Per license, over a fixed 10-minute window. Normal traffic is a few alerts an hour per
    // device; this is only there so one misbehaving install can't monopolise the APNs connection.
    [Range(1, 100_000)]
    public int PushesPerLicensePer10Minutes { get; init; } = 200;

    // A token that has not been refreshed by its owner for this long may be claimed by another
    // license - a phone that moved to a different household, or a customer who reinstalled Core
    // with a new license. Registrations are re-sent on every app launch and swept every few
    // hours by the owning Core, so a live registration never gets anywhere near this age.
    [Range(1, 3650)]
    public int StaleTokenTakeoverDays { get; init; } = 30;

    // The license signing key's public half. Defaults to the one compiled into every Core
    // install; overridable only so the tests can sign with a throwaway key. Never set this in a
    // real deployment.
    public string LicensePublicKeyPem { get; init; } = LicensePublicKey.Pem;
}
