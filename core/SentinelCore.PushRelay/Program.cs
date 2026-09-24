using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SentinelCore;
using SentinelCore.Licensing;
using SentinelCore.PushRelay;

// The vendor-operated push relay. Holds the real APNs and FCM credentials so that customer
// installs never have to; each install authenticates with its signed license and may only push
// to device tokens it registered itself. See docs/PUSH_RELAY.md.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

builder.Services.AddOptions<RelayOptions>()
    .Bind(builder.Configuration.GetSection(RelayOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => !o.Apple.Enabled || o.Apple.HasCredentials,
        "Relay:Apple credentials (TeamId, KeyId, BundleId, PrivateKeyPath) are required when Apple is enabled.")
    .Validate(
        o => !o.Firebase.Enabled || o.Firebase.HasCredentials,
        "Relay:Firebase credentials (ProjectId, ServiceAccountPath) are required when Firebase is enabled.")
    .Validate(
        o => LicenseAuthMiddleware.Authenticate("", o.LicensePublicKeyPem) is null && o.LicensePublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal),
        "Relay:LicensePublicKeyPem must be a PEM public key.")
    .ValidateOnStart();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<ApnsSender>();
builder.Services.AddSingleton<FcmSender>();
builder.Services.AddSingleton<RelayDeviceStore>();

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(RelayEndpoints.PushRateLimitPolicy, context =>
    {
        var limits = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RelayOptions>>().Value;
        // Partitioned by license, not by IP: every request arrives from the tunnel's loopback
        // hop, and one customer's burst must not cost another theirs.
        var key = (context.Items[LicenseAuthMiddleware.ItemKey] as LicenseInfo)?.LicenseId.ToString("N") ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PushesPerLicensePer10Minutes,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

app.UseMiddleware<LicenseAuthMiddleware>();
app.UseRateLimiter();
app.MapRelayEndpoints();
app.MapPublicPages();

app.Logger.LogInformation(
    "Sentinel Core push relay listening; Apple {Apple}, Firebase {Firebase}",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RelayOptions>>().Value.Apple.Enabled ? "on" : "off",
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RelayOptions>>().Value.Firebase.Enabled ? "on" : "off");

// Said at startup and again whenever appsettings.json is edited, so blocking a license shows up in
// the journal as having taken - and a mistyped id says so rather than silently blocking nobody.
var relayOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>>();
void LogBlockedLicenses(RelayOptions options)
{
    app.Logger.LogInformation("{Count} license(s) blocked", options.BlockedLicenseIds.Count - options.UnreadableBlockedLicenseIds.Count());
    foreach (var entry in options.UnreadableBlockedLicenseIds)
        app.Logger.LogWarning("Relay:BlockedLicenseIds entry '{Entry}' is not a license id and is ignored", entry);
}
LogBlockedLicenses(relayOptions.CurrentValue);
relayOptions.OnChange((options, _) => LogBlockedLicenses(options));

app.Run();

// Lets the test project reference this host through WebApplicationFactory, the same way the
// other two executables do.
public partial class Program;
