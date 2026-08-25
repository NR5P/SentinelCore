using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace SentinelCore.Tests;

// This is the only thing standing between the LAN and the alert pipeline, and the comparison it
// makes is length-then-FixedTimeEquals. That is the right shape, but it meant an unset key
// compared an empty expected value against the empty string a caller supplies by sending no
// header at all - two empty spans, which FixedTimeEquals reports as equal. A server with no key
// configured therefore authorised everybody rather than nobody.
//
// Two other things have to go wrong before that can happen on a real install - AutoApiKeyProvisioner
// writes a key on every boot when one is missing, and BridgeOptions' [Required] refuses to start
// without one - so this is the third lock rather than the first. It is here because "the key went
// missing" should never be the single failure that opens the door.
public sealed class ApiKeyMiddlewareTests
{
    private static async Task<int> StatusForAsync(string configuredKey, string? suppliedKey, string path)
    {
        var reached = false;
        var middleware = new ApiKeyMiddleware(
            _ => { reached = true; return Task.CompletedTask; },
            new StaticOptionsMonitor<BridgeOptions>(new BridgeOptions { ApiKey = configuredKey }));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (suppliedKey is not null) context.Request.Headers["X-Api-Key"] = suppliedKey;

        await middleware.InvokeAsync(context);

        // A handler that ran means the request was let through, whatever the status says.
        return reached ? StatusCodes.Status200OK : context.Response.StatusCode;
    }

    [Fact]
    public async Task WithNoKeyConfigured_ARequestWithNoKeyIsRefused()
    {
        Assert.Equal(
            (int)HttpStatusCode.Unauthorized,
            await StatusForAsync(configuredKey: "", suppliedKey: null, "/api/queue/status"));
    }

    [Fact]
    public async Task WithNoKeyConfigured_NoSuppliedKeyGetsInEither()
    {
        Assert.Equal(
            (int)HttpStatusCode.Unauthorized,
            await StatusForAsync(configuredKey: "", suppliedKey: "", "/api/queue/status"));
        Assert.Equal(
            (int)HttpStatusCode.Unauthorized,
            await StatusForAsync(configuredKey: "", suppliedKey: "guess", "/api/queue/status"));
    }

    [Fact]
    public async Task TheRightKeyGetsThrough_AndAWrongOneDoesNot()
    {
        Assert.Equal(
            StatusCodes.Status200OK,
            await StatusForAsync("the-real-key", "the-real-key", "/api/queue/status"));
        Assert.Equal(
            (int)HttpStatusCode.Unauthorized,
            await StatusForAsync("the-real-key", "the-real-kex", "/api/queue/status"));
        Assert.Equal(
            (int)HttpStatusCode.Unauthorized,
            await StatusForAsync("the-real-key", "the-real-key-with-more", "/api/queue/status"));
    }

    // Health checks sit outside /api on purpose, so a monitor can reach them without holding the
    // key that opens the alert pipeline.
    [Fact]
    public async Task AnythingOutsideApi_IsNotGatedAtAll()
    {
        Assert.Equal(StatusCodes.Status200OK, await StatusForAsync("the-real-key", null, "/health/live"));
        Assert.Equal(StatusCodes.Status200OK, await StatusForAsync("", null, "/health/live"));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
