using SentinelCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SentinelCore.Web;

// Admin-managed accounts - no self-registration (matches this app's "household tool" framing,
// just now supporting more than one named person). The very first account is created through
// POST /api/auth/enable instead of here, since these endpoints require an existing Admin
// session and there's nobody signed in yet at that point.
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/accounts").RequireAuthorization("AdminOnly");

        admin.MapGet("", async (IAccountStore accounts, CancellationToken ct) =>
        {
            var all = await accounts.GetAllAsync(ct);
            return Results.Ok(all.Select(ToDto));
        });

        admin.MapPost("", async (CreateAccountRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            var errors = AuthEndpoints.ValidateCredentials(request.Username, request.Password);
            if (!TryParseRole(request.Role, out var role))
                errors.Add("Role must be 'Admin' or 'Viewer'.");
            if (ValidateEmail(request.Email) is { } emailError)
                errors.Add(emailError);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            if (await accounts.GetByUsernameAsync(request.Username.Trim(), ct) is not null)
                return Results.BadRequest(new { errors = new[] { "That username is already in use." } });

            var account = await accounts.CreateAsync(
                request.Username.Trim(), PasswordHasher.Hash(request.Password), role, NormalizeDisplayName(request.DisplayName), ct);
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                await accounts.UpdateEmailAsync(account.Id, request.Email, ct);
                account = account with { Email = request.Email.Trim() };
            }
            return Results.Ok(ToDto(account));
        });

        // An admin setting anyone's email - where board automations send to them.
        admin.MapPut("/{id:guid}/email", async (Guid id, UpdateEmailRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            if (ValidateEmail(request.Email) is { } error)
                return Results.BadRequest(new { errors = new[] { error } });
            return await accounts.UpdateEmailAsync(id, request.Email, ct)
                ? Results.Ok(new { success = true })
                : Results.BadRequest(new { errors = new[] { "That account was not found." } });
        });

        admin.MapPut("/{id:guid}/role", async (Guid id, UpdateRoleRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            if (!TryParseRole(request.Role, out var role))
                return Results.BadRequest(new { errors = new[] { "Role must be 'Admin' or 'Viewer'." } });

            var updated = await accounts.UpdateRoleAsync(id, role, ct);
            if (!updated)
                return Results.BadRequest(new { errors = new[] { "That account was not found, or this would leave no admin accounts." } });

            await accounts.InvalidateForAccountAsync(id, ct);
            return Results.Ok(new { success = true });
        });

        admin.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            var errors = ValidateNewPassword(request.NewPassword);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            var updated = await accounts.UpdatePasswordAsync(id, PasswordHasher.Hash(request.NewPassword), ct);
            if (!updated)
                return Results.BadRequest(new { errors = new[] { "That account was not found." } });

            await accounts.InvalidateForAccountAsync(id, ct);
            return Results.Ok(new { success = true });
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IAccountStore accounts, CancellationToken ct) =>
        {
            var deleted = await accounts.DeleteAsync(id, ct);
            if (!deleted)
                return Results.BadRequest(new { errors = new[] { "That account was not found, or this would leave no admin accounts." } });

            await accounts.InvalidateForAccountAsync(id, ct);
            return Results.Ok(new { success = true });
        });

        // Self-service - any signed-in account, not just admins.
        app.MapGet("/api/accounts/me", async (HttpContext context, IAccountStore accounts, CancellationToken ct) =>
        {
            var hasId = TryGetSelfId(context, out var id);
            var account = hasId ? await accounts.GetByIdAsync(id, ct) : null;
            return Results.Ok(new
            {
                id = hasId ? id.ToString() : null,
                username = context.User.Identity?.Name,
                role = context.User.FindFirstValue(ClaimTypes.Role),
                email = account?.Email
            });
        }).RequireAuthorization();

        // Anyone may set their own email, so they can be sent board automation emails without
        // asking an admin.
        app.MapPut("/api/accounts/me/email", async (HttpContext context, UpdateEmailRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            if (!TryGetSelfId(context, out var selfId))
                return Results.BadRequest(new { errors = new[] { "No account is associated with this session." } });
            if (ValidateEmail(request.Email) is { } error)
                return Results.BadRequest(new { errors = new[] { error } });
            await accounts.UpdateEmailAsync(selfId, request.Email, ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        app.MapPost("/api/accounts/change-password", async (HttpContext context, ChangePasswordRequest request, IAccountStore accounts, CancellationToken ct) =>
        {
            if (!TryGetSelfId(context, out var selfId))
                return Results.BadRequest(new { errors = new[] { "No account is associated with this session." } });

            var account = await accounts.GetByIdAsync(selfId, ct);
            if (account is null || !PasswordHasher.Verify(request.CurrentPassword ?? "", account.PasswordHash))
                return Results.BadRequest(new { errors = new[] { "Current password is incorrect." } });

            var errors = ValidateNewPassword(request.NewPassword);
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            await accounts.UpdatePasswordAsync(selfId, PasswordHasher.Hash(request.NewPassword), ct);

            // Every session for this account, the caller's own included.
            //
            // This used to leave them all alone, reasoning that the caller had just proved the
            // current password so their session was safe to keep. That holds for the caller's
            // session and says nothing about the others - and the others are the entire point.
            // Changing your password is what someone does when they think somebody else is in
            // their account, and until now that did not remove the intruder: sessions last 30
            // days and slide, so a stolen cookie kept full access for as long as it kept being
            // used. The admin-triggered reset-password and role endpoints above already
            // invalidate for exactly this reason; self-service was the gap.
            //
            // Including the caller's own session is what keeps this simple. Trying to spare it
            // means re-establishing one mid-request, and SignInAsync on an HttpContext already
            // authenticated from the incoming cookie does not reliably persist through this
            // app's ITicketStore - StoreAsync silently never runs, and the caller leaves with a
            // Set-Cookie that looks fine and is dead. Signing everyone out, the caller included,
            // has no such trap: they log in again with the password they just chose, which is
            // what people expect of a password change anyway.
            await accounts.InvalidateForAccountAsync(selfId, ct);

            // signedOut tells the client to send the user back to the login screen rather than
            // leaving them on a page whose next request will 401 for no visible reason.
            return Results.Ok(new { success = true, signedOut = true });
        }).RequireAuthorization();
    }

    private static bool TryGetSelfId(HttpContext context, out Guid id)
    {
        var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out id);
    }

    private static bool TryParseRole(string? role, out AccountRole parsed) =>
        Enum.TryParse(role, ignoreCase: true, out parsed);

    private static List<string> ValidateNewPassword(string password)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < AuthEndpoints.MinPasswordLength)
            errors.Add($"Password must be at least {AuthEndpoints.MinPasswordLength} characters.");
        return errors;
    }

    private static string? ValidateEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || (email.Trim().Length <= 254 && SmtpMailer.LooksLikeEmailAddress(email.Trim()))
            ? null
            : "That doesn't look like an email address.";

    private static string? NormalizeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    private static object ToDto(Account account) => new
    {
        id = account.Id,
        username = account.Username,
        displayName = account.DisplayName,
        role = account.Role.ToString(),
        createdAt = account.CreatedAt,
        email = account.Email
    };
}

public sealed class CreateAccountRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}

public sealed class UpdateEmailRequest
{
    public string? Email { get; set; }
}

public sealed class UpdateRoleRequest
{
    public string Role { get; set; } = "";
}

public sealed class ResetPasswordRequest
{
    public string NewPassword { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = "";
}
