using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SentinelCore.Web;

public sealed class AuthSettingsDto
{
    public bool Enabled { get; set; }
}

// Optional dashboard login - off by default (see the "Auth" default in appsettings.json), so
// merging/deploying this never locks anyone out. Real per-person accounts (Admin/Viewer roles)
// live in IAccountStore - see AccountEndpoints.cs for creating/managing them. This file only
// covers signing in/out and turning the login requirement on/off; AuthGateMiddleware is where
// Auth:Enabled actually gets enforced (as "everyone is a synthetic Admin" while it's false).
public static class AuthEndpoints
{
    internal const int MinPasswordLength = 8;

    // Simple per-IP lockout on repeated bad logins - in-memory, no new dependency, matches the
    // rest of this feature's "hand-rolled, not framework machinery" style.
    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> FailedAttempts = new();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Always reachable, no matter the auth state - the frontend needs this to decide
        // whether to show the login overlay, and to know the current account's role once
        // signed in (so it can gate admin-only UI without a second round-trip).
        app.MapGet("/api/auth/status", async (HttpContext context, AppSettingsFile settings, IAccountStore accounts, InternetExposedSettings internetExposed, CancellationToken ct) =>
        {
            var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
            // Always required on the Internet Exposed Mode port, regardless of the base Auth
            // setting - matches AuthGateMiddleware's enforcement of the same rule.
            var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
            var enabled = isExposedRequest || (auth?.Enabled ?? false);
            var authenticated = !enabled || context.User.Identity?.IsAuthenticated == true;
            return Results.Ok(new
            {
                enabled,
                configured = await accounts.AnyAccountsExistAsync(ct),
                authenticated,
                // Only handed back once the caller has already proven they're signed in - an
                // anonymous prober gets no hint about who's using this dashboard.
                username = enabled && authenticated ? context.User.Identity?.Name : null,
                role = enabled && authenticated ? context.User.FindFirstValue(ClaimTypes.Role) : null
            });
        });

        app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AppSettingsFile settings, IAccountStore accounts, CancellationToken ct) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (IsRateLimited(ip))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var auth = await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct);
            var account = auth is { Enabled: true } ? await accounts.GetByUsernameAsync(request.Username ?? "", ct) : null;
            var valid = account is not null && PasswordHasher.Verify(request.Password ?? "", account.PasswordHash);

            if (!valid)
            {
                RecordFailure(ip);
                return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(context, account!);
            return Results.Ok(new { success = true });
        });

        // No RequireAuthorization here on purpose: signing out with an already-expired or
        // missing session should still succeed rather than 401 - it has nothing left to do.
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { success = true });
        });

        // Turns the login requirement on. While no accounts exist yet, AdminOnly trivially
        // passes for anyone (AuthGateMiddleware's synthetic Admin, since nothing has been
        // enabled yet) and this call creates the first real Admin account from the supplied
        // credentials. Once accounts exist, calling this again just flips Auth:Enabled back on
        // (e.g. after a prior disable) - no new account is created, so it can't be used to
        // sneak in a second admin, and a real Admin session is required to call it at all.
        app.MapPost("/api/auth/enable", async (HttpContext context, EnableRequest request, AppSettingsFile settings, IAccountStore accounts, CancellationToken ct) =>
        {
            if (await accounts.AnyAccountsExistAsync(ct))
            {
                await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = true }, ct);
                return Results.Ok(new { success = true });
            }

            var errors = ValidateCredentials(request.Username, request.Password);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var account = await accounts.CreateAsync(request.Username.Trim(), PasswordHasher.Hash(request.Password), AccountRole.Admin, null, ct);
            await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = true }, ct);

            await SignInAsync(context, account);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("AdminOnly");

        app.MapPost("/api/auth/disable", async (AppSettingsFile settings, IAccountStore accounts, CancellationToken ct) =>
        {
            await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = false }, ct);
            await accounts.InvalidateAllAsync(ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization("AdminOnly");
    }

    private static Task SignInAsync(HttpContext context, Account account) =>
        context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AccountPrincipal.Create(account),
            new AuthenticationProperties { IsPersistent = true });

    internal static List<string> ValidateCredentials(string username, string password)
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
