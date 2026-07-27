namespace BlueIrisOllamaBridge.Web;

// Runs after UseAuthentication() has populated context.User from any valid session cookie, and
// before UseAuthorization() evaluates each endpoint's RequireAuthorization policy. Its only job
// now: when the dashboard login is turned off entirely (Auth:Enabled=false, the shipped
// default), override context.User with a synthetic Admin principal so every
// RequireAuthorization check - including AdminOnly ones - passes for everyone, preserving
// "disabled = fully open" exactly as before the multi-account refactor. Re-reads Auth:Enabled
// fresh via AppSettingsFile on every request (same "no caching" approach LicenseEndpoints
// already uses) so flipping the setting takes effect immediately, no restart needed.
public sealed class AuthGateMiddleware(RequestDelegate next, AppSettingsFile settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", context.RequestAborted);
        if (auth is not { Enabled: true })
            context.User = AccountPrincipal.CreateSyntheticAdmin();

        await next(context);
    }
}
