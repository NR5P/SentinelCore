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
            if (errors.Count > 0)
                return Results.BadRequest(new { errors });

            if (await accounts.GetByUsernameAsync(request.Username.Trim(), ct) is not null)
                return Results.BadRequest(new { errors = new[] { "That username is already in use." } });

            var account = await accounts.CreateAsync(
                request.Username.Trim(), PasswordHasher.Hash(request.Password), role, NormalizeDisplayName(request.DisplayName), ct);
            return Results.Ok(ToDto(account));
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
        app.MapGet("/api/accounts/me", (HttpContext context) => Results.Ok(new
        {
            id = TryGetSelfId(context, out var id) ? id.ToString() : null,
            username = context.User.Identity?.Name,
            role = context.User.FindFirstValue(ClaimTypes.Role)
        })).RequireAuthorization();

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

            // Deliberately NOT calling InvalidateForAccountAsync here (unlike the admin-triggered
            // reset-password/role endpoints, which target a DIFFERENT account than the caller's
            // own and so have no continuity concern). Re-establishing a working session via
            // SignInAsync mid-request, on an HttpContext ASP.NET Core has already authenticated
            // from the incoming cookie, does not reliably persist a new entry in this app's
            // ITicketStore-backed session store - Options.SessionStore.StoreAsync silently never
            // runs, so the caller would end up with a Set-Cookie that looks fine but is dead,
            // logging them out despite the 200 response. Leaving the current session alone avoids
            // that: the caller already proved the current password above, so nothing insecure
            // about letting this one session keep working.
            return Results.Ok(new { success = true });
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

    private static string? NormalizeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    private static object ToDto(Account account) => new
    {
        id = account.Id,
        username = account.Username,
        displayName = account.DisplayName,
        role = account.Role.ToString(),
        createdAt = account.CreatedAt
    };
}

public sealed class CreateAccountRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "";
    public string? DisplayName { get; set; }
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
