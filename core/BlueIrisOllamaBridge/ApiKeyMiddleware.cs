using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace BlueIrisOllamaBridge;

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

        var expected = Encoding.UTF8.GetBytes(options.CurrentValue.ApiKey);
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
