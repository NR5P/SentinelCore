namespace BlueIrisOllamaBridge.Web;

// The pre-multi-account shape of the "Auth" config section - kept only so the one-time
// migration below can read whatever a pre-upgrade install already had. AuthSettingsDto itself
// no longer declares Username/PasswordHash; this is intentionally the only place those two
// field names are still referenced.
public sealed class LegacyAuthSettingsDto
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

// Pre-upgrade installs had a single shared Auth:Username/PasswordHash and no Accounts table at
// all. Runs once at startup: if no accounts exist yet and the legacy fields describe a
// configured login, promotes them into the first Admin account so nobody who could already log
// in loses access on this upgrade. A no-op on every subsequent startup (AnyAccountsExistAsync
// short-circuits it), and a no-op for the common case too - most installs never turned
// Auth:Enabled on, so there's nothing to migrate; the first admin there is created explicitly
// instead, via POST /api/auth/enable.
public static class AccountMigration
{
    public static async Task MigrateLegacyAuthIfNeededAsync(IAccountStore accounts, AppSettingsFile settings, CancellationToken cancellationToken)
    {
        if (await accounts.AnyAccountsExistAsync(cancellationToken))
            return;

        var legacy = await settings.GetSectionAsync<LegacyAuthSettingsDto>("Auth", cancellationToken);
        if (legacy is not { Enabled: true } || string.IsNullOrEmpty(legacy.PasswordHash) || string.IsNullOrWhiteSpace(legacy.Username))
            return;

        // Copied as-is - PasswordHasher.Verify re-derives using whatever iteration count is
        // embedded in the stored value, so no re-hash or forced reset is needed.
        await accounts.CreateAsync(legacy.Username, legacy.PasswordHash, AccountRole.Admin, null, cancellationToken);
    }
}
