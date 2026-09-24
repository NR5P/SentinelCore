using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace SentinelCore.Web;

public sealed class AuthSettingsDto
{
    // Missing means required. A dashboard that anyone on the network (or any web page one of
    // them opens) can administer is not a safe thing to hand a stranger, so the only way to run
    // without a login is to say so: an admin turning it off in SETTINGS, which writes an explicit
    // false here.
    public bool Enabled { get; set; } = true;

    public static async Task<bool> IsLoginRequiredAsync(AppSettingsFile settings, CancellationToken ct) =>
        (await settings.GetSectionAsync<AuthSettingsDto>("Auth", ct))?.Enabled ?? true;
}

// Dashboard login - required from the first launch. Until the first Admin account exists the
// only thing the API will do is create it (POST /api/auth/enable, from the local network only),
// which is what the dashboard's first screen asks for. Real per-person accounts (Admin/Viewer
// roles) live in IAccountStore - see AccountEndpoints.cs for creating/managing them. This file
// covers signing in/out and turning the login requirement on/off; AuthGateMiddleware is where
// Auth:Enabled actually gets enforced (as "everyone is a synthetic Admin" once an admin has
// explicitly turned it off).
public static class AuthEndpoints
{
    internal const int MinPasswordLength = 8;

    public const string NoAdminYetMessage =
        "This server has no accounts yet. Open its web dashboard in a browser on your home network to create the admin account, then sign in here.";

    private static readonly SemaphoreSlim FirstAdminGate = new(1, 1);

    // Simple per-IP lockout on repeated bad logins - in-memory, no new dependency, matches the
    // rest of this feature's "hand-rolled, not framework machinery" style.
    private const int MaxFailuresPerWindow = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> FailedAttempts = new();

    // How many distinct addresses may accumulate before the sweep below starts running, and how
    // often it may run at most. See SweepStaleAttemptsIfDue.
    private const int SweepThreshold = 1_000;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static long _lastSweepTicks;

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Always reachable, no matter the auth state - the frontend needs this to decide
        // whether to show the login overlay, and to know the current account's role once
        // signed in (so it can gate admin-only UI without a second round-trip).
        app.MapGet("/api/auth/status", async (HttpContext context, AppSettingsFile settings, IAccountStore accounts, InternetExposedSettings internetExposed, CancellationToken ct) =>
        {
            // Always required on the Internet Exposed Mode port, regardless of the base Auth
            // setting - matches AuthGateMiddleware's enforcement of the same rule.
            var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
            var enabled = isExposedRequest || await AuthSettingsDto.IsLoginRequiredAsync(settings, ct);
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

        app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, AppSettingsFile settings, IAccountStore accounts, InternetExposedSettings internetExposed, ILoginAlertSender loginAlerts, ILogger<LoginRequest> logger, CancellationToken ct) =>
        {
            var ip = ClientIpResolver.Resolve(context, internetExposed);
            var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
            var via = isExposedRequest ? "internet-exposed" : "LAN";

            if (IsRateLimited(ip))
            {
                logger.LogWarning("Login rate-limited for {Username} from {Ip} ({Via}) after repeated failures", request.Username, ip, via);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            // Auth:Enabled=false means "nobody has to sign in", so there's no account to look up.
            // The Internet Exposed port is the exception: it always requires login regardless of
            // that setting - both AuthGateMiddleware and /api/auth/status already treat it that
            // way - so it has to be able to sign in while the base setting is off. Without this,
            // that port serves a login screen no correct password can ever get past.
            var loginRequired = isExposedRequest || await AuthSettingsDto.IsLoginRequiredAsync(settings, ct);

            // Not a wrong password - there is nothing to sign in to yet, and the apps can't create
            // the first account, so the text has to say where to go. 409 rather than 401 on
            // purpose: every client replaces a 401 with its own "sign in" wording, but shows any
            // other error's text as-is.
            if (loginRequired && !await accounts.AnyAccountsExistAsync(ct))
                return Results.Json(new { error = NoAdminYetMessage }, statusCode: StatusCodes.Status409Conflict);

            // Trimmed to match CreateAsync, which stores the username trimmed: a soft keyboard
            // that tacks a space onto the field shouldn't read as a wrong password.
            var account = loginRequired ? await accounts.GetByUsernameAsync((request.Username ?? "").Trim(), ct) : null;
            var valid = account is not null && PasswordHasher.Verify(request.Password ?? "", account.PasswordHash);

            if (!valid)
            {
                var failures = RecordFailure(ip);
                // Username quoted on purpose: the most confusing way for this to fail is a
                // keyboard-added space, which is invisible in an unquoted log line.
                logger.LogWarning("Login failed for '{Username}' from {Ip} ({Via})", request.Username, ip, via);

                // Fire-and-forget: LoginAlertSender swallows its own errors, and a mistyped
                // password shouldn't make the caller wait out an SMTP/Telegram round trip.
                // CancellationToken.None on purpose - the request's own token is tied to this
                // response, which returns immediately, but the send should still complete.
                _ = loginAlerts.NotifyInvalidLoginAsync(request.Username ?? "", ip, via, failures, CancellationToken.None);

                return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(context, account!);
            logger.LogInformation("Login succeeded for {Username} ({Role}) from {Ip} ({Via})", account!.Username, account.Role, ip, via);
            return Results.Ok(new { success = true });
        });

        // No RequireAuthorization here on purpose: signing out with an already-expired or
        // missing session should still succeed rather than 401 - it has nothing left to do.
        app.MapPost("/api/auth/logout", async (HttpContext context, InternetExposedSettings internetExposed, ILogger<LoginRequest> logger) =>
        {
            var username = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null;
            if (username is not null)
            {
                var ip = ClientIpResolver.Resolve(context, internetExposed);
                logger.LogInformation("Logout for {Username} from {Ip}", username, ip);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { success = true });
        });

        // Two jobs. On a fresh install - no accounts at all - it creates the first Admin account
        // from the supplied credentials and signs the caller in. That has to work for an anonymous
        // caller, since nobody can have signed in yet, so it is the one door left open before
        // then; it is limited to the local network (never the Internet Exposed port) so the
        // server can't be claimed from outside before its owner gets to it. Once accounts exist,
        // it just flips Auth:Enabled back on after a prior disable - no new account is created,
        // so it can't be used to sneak in a second admin, and it takes an Admin session.
        app.MapPost("/api/auth/enable", async (HttpContext context, EnableRequest request, AppSettingsFile settings, IAccountStore accounts, InternetExposedSettings internetExposed, IAuthorizationService authorization, ILogger<LoginRequest> logger, CancellationToken ct) =>
        {
            var ip = ClientIpResolver.Resolve(context, internetExposed);

            // Serialized so two first-run requests racing each other can't both see "no accounts"
            // and both create an Admin.
            await FirstAdminGate.WaitAsync(ct);
            try
            {
                if (await accounts.AnyAccountsExistAsync(ct))
                {
                    if (!(await authorization.AuthorizeAsync(context.User, "AdminOnly")).Succeeded)
                        return context.User.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();

                    await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = true }, ct);
                    logger.LogInformation("Login requirement turned on from {Ip}", ip);
                    return Results.Ok(new { success = true });
                }

                var isExposedRequest = internetExposed.Enabled && context.Connection.LocalPort == internetExposed.Port;
                if (isExposedRequest || !ExposureTracker.IsLocalNetwork(context.Connection.RemoteIpAddress))
                {
                    logger.LogWarning("Refused to create the first admin account for a request from {Ip}, which is outside the local network", ip);
                    return Results.Json(new { errors = new[] { "The first admin account can only be created from a device on the same network as this server." } }, statusCode: StatusCodes.Status403Forbidden);
                }

                var errors = ValidateCredentials(request.Username, request.Password);
                if (errors.Count > 0)
                    return Results.BadRequest(new { errors });

                var account = await accounts.CreateAsync(request.Username.Trim(), PasswordHasher.Hash(request.Password), AccountRole.Admin, null, ct);
                await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = true }, ct);

                await SignInAsync(context, account);
                logger.LogInformation("First admin account {Username} created from {Ip}", account.Username, ip);
                return Results.Ok(new { success = true });
            }
            finally
            {
                FirstAdminGate.Release();
            }
        });

        app.MapPost("/api/auth/disable", async (HttpContext context, AppSettingsFile settings, IAccountStore accounts, InternetExposedSettings internetExposed, ILogger<LoginRequest> logger, CancellationToken ct) =>
        {
            var ip = ClientIpResolver.Resolve(context, internetExposed);
            await settings.MergeSectionAsync("Auth", new AuthSettingsDto { Enabled = false }, ct);
            await accounts.InvalidateAllAsync(ct);
            logger.LogWarning("Login requirement turned off from {Ip} - the dashboard is now open to anyone who can reach it", ip);
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

    // Returns the number of failures still within the window after recording this one, so the
    // login-alert email/Telegram message can say "this is attempt #N", not just "invalid login".
    private static int RecordFailure(string ip)
    {
        var attempts = FailedAttempts.GetOrAdd(ip, static _ => []);
        int count;
        lock (attempts)
        {
            attempts.RemoveAll(t => t < DateTimeOffset.UtcNow - FailureWindow);
            attempts.Add(DateTimeOffset.UtcNow);
            count = attempts.Count;
        }

        SweepStaleAttemptsIfDue();
        return count;
    }

    // An address that fails once and never comes back used to keep its entry for the lifetime of
    // the process: the pruning above only ever runs for an address that is seen again. One entry
    // per source address is nothing on the LAN, where the same handful of machines fail a login
    // now and then - but Internet Exposed Mode puts this endpoint in front of the open internet,
    // where a distributed attempt means an entry per source address and no reason for any of them
    // to return.
    //
    // Bounded both ways on purpose. The size check keeps ordinary use from ever walking the
    // dictionary, and the interval keeps a sustained attack from turning every failed login into
    // an O(n) sweep - which would hand an attacker a cheaper way to load the server than the
    // brute force itself.
    private static void SweepStaleAttemptsIfDue()
    {
        if (FailedAttempts.Count <= SweepThreshold) return;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < SweepInterval.Ticks) return;
        // Whoever wins this exchange does the sweep; everyone else carries on.
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;

        var cutoff = DateTimeOffset.UtcNow - FailureWindow;
        foreach (var (address, attempts) in FailedAttempts)
        {
            lock (attempts)
            {
                attempts.RemoveAll(t => t < cutoff);
                // Keyed removal, so an address that started failing again while this ran keeps
                // the entry the other thread is holding rather than losing it here. The narrow
                // race left is a failure recorded against a list already removed, which costs an
                // attacker's next attempt one off its count - immaterial to a limiter that only
                // has to be approximately right.
                if (attempts.Count == 0)
                    FailedAttempts.TryRemove(new KeyValuePair<string, List<DateTimeOffset>>(address, attempts));
            }
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
