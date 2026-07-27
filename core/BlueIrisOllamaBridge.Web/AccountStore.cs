using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;

namespace BlueIrisOllamaBridge.Web;

// Real, distinct accounts for the dashboard login - replaces the old single shared
// Username/PasswordHash in AuthSettingsDto. Stored in its own SQLite file, same reasoning as
// CameraStore/TriggerStore: this is a .Web-only concept the always-on bridge process never
// needs to know about.
public enum AccountRole { Admin, Viewer }

public sealed record Account(
    Guid Id,
    string Username,
    string PasswordHash,
    AccountRole Role,
    string? DisplayName,
    DateTimeOffset CreatedAt);

// Shared claim shape between sign-in (AuthEndpoints), session re-derivation (RetrieveAsync
// below), and the "login not required" bootstrap (AuthGateMiddleware) - one place defines what
// a signed-in principal looks like.
public static class AccountPrincipal
{
    public static ClaimsPrincipal Create(Account account)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString("N")),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, account.Role.ToString())
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    // Used only when Auth:Enabled is false - grants full (Admin) access to every request with
    // no real account behind it, preserving "disabled = fully open" exactly as before.
    public static ClaimsPrincipal CreateSyntheticAdmin()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "(no login required)"),
            new Claim(ClaimTypes.Role, nameof(AccountRole.Admin))
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}

public interface IAccountStore
{
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken);
    Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken);
    Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Account> CreateAsync(string username, string passwordHash, AccountRole role, string? displayName, CancellationToken cancellationToken);

    // False means the change was refused because it would leave zero admins - never throws for
    // that case, since it's an expected, recoverable outcome the endpoint turns into a 400.
    Task<bool> UpdateRoleAsync(Guid id, AccountRole role, CancellationToken cancellationToken);
    Task<bool> UpdatePasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Drives the one-time legacy-auth migration: only ever true before the first account is
    // created, so the migration stays a no-op on every subsequent startup.
    Task<bool> AnyAccountsExistAsync(CancellationToken cancellationToken);

    // Forced logout - e.g. after a reset-password or role change, so a stolen-but-still-valid
    // session for that account can't keep working.
    Task InvalidateForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    // Used when login is disabled entirely - every outstanding session becomes moot at that
    // point, so there's no reason to let any of them linger.
    Task InvalidateAllAsync(CancellationToken cancellationToken);
}

// Also the ITicketStore backing the dashboard's cookie authentication (see Program.cs) -
// sessions live in the same database as accounts (one connection, one file), the same way
// SqliteTriggerStore already backs two interfaces from one instance in this codebase. Kept on
// this class rather than a separate SessionStore/db so a deleted account's sessions can never
// outlive the account (RetrieveAsync re-fetches the live account on every request - see below).
public sealed class SqliteAccountStore : IAccountStore, ITicketStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAccountStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var dbPath = Path.Combine(dataDirectory, "accounts.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Accounts (
                Id TEXT PRIMARY KEY,
                Username TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                Role TEXT NOT NULL,
                DisplayName TEXT,
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Accounts_Username ON Accounts(Username COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS Sessions (
                Token TEXT PRIMARY KEY,
                AccountId TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Username, PasswordHash, Role, DisplayName, CreatedAt FROM Accounts ORDER BY Username COLLATE NOCASE ASC;";

            var results = new List<Account>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadAccount(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Account?> GetByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, Username, PasswordHash, Role, DisplayName, CreatedAt FROM Accounts WHERE Username = $username COLLATE NOCASE;";
            command.Parameters.AddWithValue("$username", username);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await GetByIdNoLockAsync(id, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Account> CreateAsync(string username, string passwordHash, AccountRole role, string? displayName, CancellationToken cancellationToken)
    {
        var account = new Account(Guid.NewGuid(), username, passwordHash, role, displayName, DateTimeOffset.UtcNow);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Accounts (Id, Username, PasswordHash, Role, DisplayName, CreatedAt)
                VALUES ($id, $username, $passwordHash, $role, $displayName, $createdAt);
                """;
            AddParameters(command, account);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        return account;
    }

    public async Task<bool> UpdateRoleAsync(Guid id, AccountRole role, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetByIdNoLockAsync(id, cancellationToken);
            if (existing is null) return false;
            if (existing.Role == role) return true;

            // Refuse from inside the same lock that reads the current role, so two concurrent
            // admin actions can't both pass this check and leave zero admins between them.
            if (existing.Role == AccountRole.Admin && role == AccountRole.Viewer && await CountAdminsNoLockAsync(cancellationToken) <= 1)
                return false;

            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Accounts SET Role = $role WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$role", role.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> UpdatePasswordAsync(Guid id, string passwordHash, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Accounts SET PasswordHash = $passwordHash WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$passwordHash", passwordHash);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var existing = await GetByIdNoLockAsync(id, cancellationToken);
            if (existing is null) return false;

            if (existing.Role == AccountRole.Admin && await CountAdminsNoLockAsync(cancellationToken) <= 1)
                return false;

            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Accounts WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> AnyAccountsExistAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM Accounts);";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(result) != 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    // --- ITicketStore: backs the cookie authentication handler's server-side session storage ---

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var accountId = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Cannot store a session for a ticket with no account id claim.");
        var expiresAt = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddDays(30);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        await _lock.WaitAsync();
        try
        {
            SweepExpiredNoLock();

            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO Sessions (Token, AccountId, ExpiresAt) VALUES ($token, $accountId, $expiresAt);";
            command.Parameters.AddWithValue("$token", token);
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$expiresAt", expiresAt.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }

        return token;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var expiresAt = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddDays(30);

        await _lock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE Sessions SET ExpiresAt = $expiresAt WHERE Token = $token;";
            command.Parameters.AddWithValue("$token", key);
            command.Parameters.AddWithValue("$expiresAt", expiresAt.UtcDateTime.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Re-derives the principal from the account's CURRENT role/username on every request rather
    // than trusting whatever was true at sign-in time - a role change or account deletion by an
    // admin takes effect on the very next request for that person, with no separate invalidation
    // needed for the common case (only the last-admin-guard-adjacent forced logouts below need
    // to act early, e.g. so a stolen-but-still-valid token doesn't outlive a password reset).
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        Guid accountId;
        await _lock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT AccountId, ExpiresAt FROM Sessions WHERE Token = $token;";
            command.Parameters.AddWithValue("$token", key);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var expiresAt = DateTimeOffset.Parse(reader.GetString(1));
            if (expiresAt < DateTimeOffset.UtcNow)
                return null;

            accountId = Guid.ParseExact(reader.GetString(0), "N");
        }
        finally
        {
            _lock.Release();
        }

        var account = await GetByIdAsync(accountId, CancellationToken.None);
        if (account is null)
            return null;

        var properties = new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) };
        return new AuthenticationTicket(AccountPrincipal.Create(account), properties, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task RemoveAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions WHERE Token = $token;";
            command.Parameters.AddWithValue("$token", key);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Admin-triggered forced logout - e.g. after a reset-password or role change, so a
    // stolen-but-still-valid session for that account can't keep working.
    public async Task InvalidateForAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions WHERE AccountId = $accountId;";
            command.Parameters.AddWithValue("$accountId", accountId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Used when login is disabled entirely - every outstanding session becomes moot at that
    // point, so there's no reason to let any of them linger.
    public async Task InvalidateAllAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Sessions;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void SweepExpiredNoLock()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE ExpiresAt < $now;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private async Task<Account?> GetByIdNoLockAsync(Guid id, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Id, Username, PasswordHash, Role, DisplayName, CreatedAt FROM Accounts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAccount(reader) : null;
    }

    private async Task<int> CountAdminsNoLockAsync(CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Accounts WHERE Role = $role;";
        command.Parameters.AddWithValue("$role", AccountRole.Admin.ToString());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static void AddParameters(SqliteCommand command, Account account)
    {
        command.Parameters.AddWithValue("$id", account.Id.ToString("N"));
        command.Parameters.AddWithValue("$username", account.Username);
        command.Parameters.AddWithValue("$passwordHash", account.PasswordHash);
        command.Parameters.AddWithValue("$role", account.Role.ToString());
        command.Parameters.AddWithValue("$displayName", (object?)account.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", account.CreatedAt.UtcDateTime.ToString("O"));
    }

    private static Account ReadAccount(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<AccountRole>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5)));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
