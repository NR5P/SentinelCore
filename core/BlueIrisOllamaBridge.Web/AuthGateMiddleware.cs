namespace BlueIrisOllamaBridge.Web;

// Runs after UseAuthentication() has populated context.User from any valid session cookie, and
// before UseAuthorization() evaluates each endpoint's RequireAuthorization policy. Its only job
// now: when the dashboard login is turned off entirely (Auth:Enabled=false, the shipped
// default), override context.User with a synthetic Admin principal so every
// RequireAuthorization check - including AdminOnly ones - passes for everyone, preserving
// "disabled = fully open" exactly as before the multi-account refactor. Re-reads Auth:Enabled
// fresh via AppSettingsFile on every request (same "no caching" approach LicenseEndpoints
// already uses) so flipping the setting takes effect immediately, no restart needed.
//
// Login is also always required - regardless of Auth:Enabled - for any request that arrived on
// the Internet Exposed Mode port (see InternetExposedSettings): that port only exists for
// traffic reaching this server from outside the LAN, so it must never fall back to the
// synthetic-Admin bypass no matter how the base Auth setting is configured.
public sealed class AuthGateMiddleware(RequestDelegate next, AppSettingsFile settings, InternetExposedSettings internetExposed)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
        var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", context.RequestAborted);
        if (!isExposedRequest && auth is not { Enabled: true })
            context.User = AccountPrincipal.CreateSyntheticAdmin();

        await next(context);
    }
}
