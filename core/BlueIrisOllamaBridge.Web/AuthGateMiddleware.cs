namespace BlueIrisOllamaBridge.Web;

// Mirrors BlueIrisOllamaBridge/ApiKeyMiddleware.cs's shape, but for the dashboard's own,
// separate, optional login - re-reads Auth:Enabled fresh via AppSettingsFile on every request
// (same "no caching" approach LicenseEndpoints already uses) so flipping the setting takes
// effect immediately, no restart needed. Static files are never touched here - only
// UseStaticFiles() serves those, so the SPA shell always loads; what's gated is the data it
// fetches under /api. When Auth:Enabled is false (the shipped default), this is a pure no-op.
public sealed class AuthGateMiddleware(RequestDelegate next, AppSettingsFile settings, SessionStore sessions)
{
    // Reachable with no session so the frontend can always ask "do I need to log in?" and so
    // login itself is possible in the first place. Nothing else under /api is exempt.
    private static readonly string[] AlwaysOpenPaths = ["/api/auth/status", "/api/auth/login", "/api/health"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || AlwaysOpenPaths.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", context.RequestAborted);
        if (auth is not { Enabled: true })
        {
            await next(context);
            return;
        }

        if (!sessions.Validate(context.Request.Cookies["session"]))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Not signed in." });
            return;
        }

        await next(context);
    }
}
