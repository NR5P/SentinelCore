using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SentinelCore.Licensing;

namespace SentinelCore.PushRelay;

// Every /v1 request carries the caller's license file - the same .lic the dashboard installs -
// base64-encoded in one header. Verified here with the public key, so a request that reaches an
// endpoint is already known to come from a real, unforged license; the LicenseId is then what
// keeps one install's devices apart from another's.
public sealed class LicenseAuthMiddleware(RequestDelegate next, IOptions<RelayOptions> options)
{
    public const string HeaderName = "X-Sentinel-License";
    public const string ItemKey = "SentinelCore.License";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var license = Authenticate(context.Request.Headers[HeaderName].ToString(), options.Value.LicensePublicKeyPem);
        if (license is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "A valid Sentinel Core license is required. Install one in the dashboard under Settings → LICENSE."
            });
            return;
        }

        context.Items[ItemKey] = license;
        await next(context);
    }

    public static LicenseInfo? Authenticate(string headerValue, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > 16 * 1024) return null;
        try
        {
            var contents = Encoding.UTF8.GetString(Convert.FromBase64String(headerValue.Trim()));
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(publicKeyPem);
            return LicenseVerifier.Verify(contents, publicKey);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return null;
        }
    }
}

public static class HttpContextLicenseExtensions
{
    public static LicenseInfo License(this HttpContext context) =>
        context.Items[LicenseAuthMiddleware.ItemKey] as LicenseInfo
        ?? throw new InvalidOperationException("Endpoint reached without a verified license - is LicenseAuthMiddleware registered?");
}
