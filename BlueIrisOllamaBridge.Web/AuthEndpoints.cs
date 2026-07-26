using System.Collections.Concurrent;

namespace BlueIrisOllamaBridge.Web;

public sealed class AuthSettingsDto
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

// Optional dashboard login - off by default (see the "Auth" default in appsettings.json), so
// merging/deploying this never locks anyone out. A single shared username/password, not
// multi-user accounts - this is a household tool, not a team product. See AuthGateMiddleware
// for where this actually gets enforced; these endpoints are the only way Auth:Enabled/
// Username/PasswordHash ever change.
public static class AuthEndpoints
{
    private const int MinPasswordLength = 8;

    // Simple per-IP lockout on repeated bad logins - in-memory, no new dependency, matches the
    // rest of this feature's "hand-rolled, not framework machinery" style.
    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> FailedAttempts = new();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Always reachable, even with no session - the frontend needs this to decide whether
        // to show the login overlay at all. `authenticated` reflects THIS request's own
        // session cookie (true whenever login isn't required in the first place) - safe to
        // expose unauthenticated since it reveals nothing beyond "is this specific browser
        // already signed in," not who the account belongs to.
        app.MapGet("/api/auth/status", async (HttpContext context, AppSettingsFile settings, SessionStore sessions, CancellationToken ct) =>
        {
            var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
            var enabled = auth?.Enabled ?? false;
            var authenticated = !enabled || sessions.Validate(context.Request.Cookies["session"]);
            return Results.Ok(new
            {
                enabled,
                configured = !string.IsNullOrEmpty(auth?.PasswordHash),
                authenticated,
                // Only handed back once the caller has already proven they're signed in - an
                // anonymous prober gets no hint about who the account belongs to.
                username = enabled && authenticated ? auth?.Username : null
            });
        });

        app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AppSettingsFile settings, SessionStore sessions, CancellationToken ct) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (IsRateLimited(ip))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
            var valid = auth is { Enabled: true }
                && string.Equals(auth.Username, request.Username, StringComparison.Ordinal)
                && PasswordHasher.Verify(request.Password ?? "", auth.PasswordHash);

            if (!valid)
            {
                RecordFailure(ip);
                return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            SetSessionCookie(context, sessions.CreateSession());
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/auth/logout", (HttpContext context, SessionStore sessions) =>
        {
            sessions.Invalidate(context.Request.Cookies["session"]);
            context.Response.Cookies.Delete("session");
            return Results.Ok(new { success = true });
        });

        // Only reachable while Auth:Enabled is false - AuthGateMiddleware passes everything
        // through untouched in that state, which is exactly the state this endpoint needs to
        // be callable in (there's no session to require yet). Once this flips Enabled to true,
        // every subsequent call - including a second call to this same endpoint - goes through
        // the normal gate like anything else.
        app.MapPost("/api/auth/enable", async (HttpContext context, EnableRequest request, AppSettingsFile settings, SessionStore sessions, CancellationToken ct) =>
        {
            var errors = ValidateCredentials(request.Username, request.Password);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            await settings.MergeSectionAsync("Auth", new AuthSettingsDto
            {
                Enabled = true,
                Username = request.Username.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password)
            }, ct);

            SetSessionCookie(context, sessions.CreateSession());
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/auth/update", async (HttpContext context, UpdateRequest request, AppSettingsFile settings, SessionStore sessions, CancellationToken ct) =>
        {
            var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
            if (auth is null || !PasswordHasher.Verify(request.CurrentPassword ?? "", auth.PasswordHash))
                return Results.BadRequest(new { errors = new[] { "Current password is incorrect." } });

            var newUsername = string.IsNullOrWhiteSpace(request.NewUsername) ? auth.Username : request.NewUsername.Trim();
            var newPassword = string.IsNullOrEmpty(request.NewPassword) ? null : request.NewPassword;

            if (newPassword is not null)
            {
                var errors = ValidateCredentials(newUsername, newPassword);
                if (errors.Count > 0)
                    return Results.BadRequest(new { errors });
            }

            await settings.MergeSectionAsync("Auth", new AuthSettingsDto
            {
                Enabled = true,
                Username = newUsername,
                PasswordHash = newPassword is null ? auth.PasswordHash : PasswordHasher.Hash(newPassword)
            }, ct);

            // A credential change invalidates every existing session (including this one's old
            // token) before issuing a fresh one - a stolen-but-still-open session shouldn't
            // survive a rotation meant to shut it out, but the person making the change is
            // never logged out by their own action.
            sessions.InvalidateAll();
            SetSessionCookie(context, sessions.CreateSession());
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/auth/disable", async (AppSettingsFile settings, SessionStore sessions, CancellationToken ct) =>
        {
            await settings.MergeSectionAsync("Auth", new { Enabled = false }, ct);
            sessions.InvalidateAll();
            return Results.Ok(new { success = true });
        });
    }

    private static void SetSessionCookie(HttpContext context, string token)
    {
        context.Response.Cookies.Append("session", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/"
        });
    }

    private static List<string> ValidateCredentials(string username, string password)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(username))
            errors.Add("Username is required.");
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            errors.Add($"Password must be at least {MinPasswordLength} characters.");
        return errors;
    }

    private static bool IsRateLimited(string ip)
    {
        if (!FailedAttempts.TryGetValue(ip, out var attempts)) return false;
        lock (attempts)
        {
            attempts.RemoveAll(t => t < DateTimeOffset.UtcNow - FailureWindow);
            return attempts.Count >= MaxFailuresPerWindow;
        }
    }

    private static void RecordFailure(string ip)
    {
        var attempts = FailedAttempts.GetOrAdd(ip, static _ => []);
        lock (attempts)
        {
            attempts.Add(DateTimeOffset.UtcNow);
        }
    }
}

public sealed class LoginRequest
{
    public string Username { get; set; } = "";
    public string? Password { get; set; }
}

public sealed class EnableRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class UpdateRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewUsername { get; set; }
    public string? NewPassword { get; set; }
}
