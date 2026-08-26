using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SentinelCore.Client.Networking;

public sealed record StoredCredential(string Username, string Password);

/// <summary>
/// Where the dashboard login is kept between runs, so a session that expires overnight renews
/// itself instead of showing a sign-in screen every morning. The Apple client uses the Keychain;
/// the Windows equivalent is DPAPI, which encrypts against the signed-in Windows account - another
/// user on the same PC (and anyone reading the file off a stolen disk without that account's
/// credentials) gets ciphertext.
/// </summary>
/// <remarks>
/// Stored per server address, not once globally: two servers are two different logins, and reusing
/// one against the other would send the wrong password to a machine that might well log it.
/// </remarks>
public sealed class CredentialStore
{
    private readonly string _directory;

    public CredentialStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentinelCore",
            "credentials");
    }

    public void Save(StoredCredential credential, string serverUrl)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.SerializeToUtf8Bytes(credential);
        var sealedBytes = Protect(json);
        // Written to a temporary file and moved into place, so an interrupted write can't leave a
        // half-written blob that fails to decrypt on the next launch - the same rule the server
        // applies to its HomeKit identity.
        var path = PathFor(serverUrl);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedBytes);
        File.Move(temporary, path, overwrite: true);
    }

    public StoredCredential? Load(string serverUrl)
    {
        var path = PathFor(serverUrl);
        if (!File.Exists(path)) return null;
        try
        {
            var json = Unprotect(File.ReadAllBytes(path));
            return JsonSerializer.Deserialize<StoredCredential>(json);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException)
        {
            // A blob that no longer decrypts (a restored profile, a corrupted file) is not worth
            // an error path of its own: it means the same thing as having no saved login at all,
            // which is a normal sign-in screen.
            return null;
        }
    }

    public void Delete(string serverUrl)
    {
        var path = PathFor(serverUrl);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing useful to do about it, and the caller is usually signing out - which must
            // not fail because a file was locked.
        }
    }

    private string PathFor(string serverUrl)
    {
        var normalized = (ServerAddress.Normalize(serverUrl) ?? serverUrl).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
        return Path.Combine(_directory, $"{hash}.bin");
    }

    // Virtual seams so the tests can round-trip the file format without depending on the DPAPI of
    // whichever account happens to be running them.
    internal Func<byte[], byte[]> Protect { get; init; } =
        data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    internal Func<byte[], byte[]> Unprotect { get; init; } =
        data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
