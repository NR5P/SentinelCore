using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SentinelCore;

public sealed class ApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<BridgeOptions> options)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var configured = options.CurrentValue.ApiKey;

        // Fail closed on a missing key rather than open. With no key configured, `expected` is
        // empty, a request with no X-Api-Key header supplies an empty value too, and
        // FixedTimeEquals compares two empty spans and says yes - so an unset key authorised
        // everybody instead of nobody. AutoApiKeyProvisioner makes sure one is always set, so in
        // practice this changes nothing; it is here so that "the key went missing" can never be
        // the one failure that opens the door.
        if (string.IsNullOrEmpty(configured))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "This server has no API key configured." });
            return;
        }

        var expected = Encoding.UTF8.GetBytes(configured);
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        var actual = Encoding.UTF8.GetBytes(supplied);
        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key." });
            return;
        }
        await _next(context);
    }
}
