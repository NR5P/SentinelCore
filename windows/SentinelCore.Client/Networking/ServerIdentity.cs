using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SentinelCore.Client.Networking;

/// <summary>What <see cref="ServerIdentity.CheckAsync"/> found at an https:// address.</summary>
public enum IdentityVerdict
{
    /// <summary>Completed a handshake with a certificate the remembered authority signed.</summary>
    Verified,

    /// <summary>Something answered with a certificate the remembered authority didn't sign.</summary>
    Mismatch,

    /// <summary>Nothing remembered for this address yet; <see cref="IdentityCheck.Leaf"/> is what
    /// it showed, to check the authority against once the fallback has worked.</summary>
    Unknown,

    /// <summary>No certificate to judge - the address stopped answering in between.</summary>
    Unavailable,
}

/// <param name="Completed">Whether the handshake was completed and answered - proof the server holds
/// the certificate's key, not just a copy of it.</param>
public sealed record IdentityCheck(IdentityVerdict Verdict, X509Certificate2? Leaf = null, bool Completed = false);

/// <summary>
/// Recognising this app's own server when Windows doesn't trust its HTTPS certificate. A port of
/// ios/SentinelCore/Networking/ServerIdentity.swift.
/// </summary>
/// <remarks>
/// The server makes its own certificate authority, so AppModel falls back to the plain-HTTP port
/// when the https:// address fails trust. Taken for anything that fails trust, that fallback would
/// hand the session cookie - and the saved password, on the automatic re-login - to whatever
/// answers at the same private IP on someone else's network. So the server's authority is
/// remembered the first time the fallback works (trust on first use - in practice at home, during
/// setup), and after that the fallback only counts as this server when it completes a TLS
/// handshake with a certificate that authority signed. Completing the handshake, not just showing
/// the certificate, is what proves it holds the key. The check sends nothing: no cookies, and the
/// handshake is abandoned before the request whenever the certificate doesn't verify.
///
/// It is the authority that's remembered because the server reissues its own certificate (with a
/// new key) whenever its IP address changes, but keeps the authority. A reinstalled server has a
/// new one and is refused until its address is saved again in Settings (see <see cref="Forget"/>).
/// </remarks>
public sealed class ServerIdentity
{
    private const string KeyPrefix = "serverIdentity.authority.";
    private readonly PreferenceStore _preferences;

    public ServerIdentity(PreferenceStore preferences) => _preferences = preferences;

    /// <param name="knownLeafThumbprint">
    /// With nothing remembered, a certificate seen earlier this session that may complete the
    /// handshake - so a server whose authority couldn't be fetched (one older than
    /// /api/certificate-authority) can still be told apart from a copy of its certificate.
    /// </param>
    public async Task<IdentityCheck> CheckAsync(
        string httpsAddress, CancellationToken ct = default, string? knownLeafThumbprint = null)
    {
        if (ServerAddress.Normalize(httpsAddress) is not { } normalized ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            return new IdentityCheck(IdentityVerdict.Unavailable);
        }

        var authority = RememberedAuthority(uri);
        X509Certificate2? leaf = null;
        var chained = false;
        using var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            // Finish the handshake only for our own authority's certificate; anything else is
            // abandoned before a single byte of the request is sent.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                leaf = new X509Certificate2(certificate.RawData);
                chained = authority is not null
                    ? SignedBy(authority, leaf)
                    : knownLeafThumbprint is not null && leaf.Thumbprint == knownLeafThumbprint;
                return chained;
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        bool answered;
        try
        {
            using var response = await http.GetAsync($"{normalized}/api/health", ct).ConfigureAwait(false);
            answered = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            answered = false;
        }

        if (leaf is null) return new IdentityCheck(IdentityVerdict.Unavailable);
        if (authority is null) return new IdentityCheck(IdentityVerdict.Unknown, leaf, chained && answered);
        if (chained) return new IdentityCheck(answered ? IdentityVerdict.Verified : IdentityVerdict.Unavailable, leaf, answered);
        return new IdentityCheck(IdentityVerdict.Mismatch, leaf);
    }

    /// <summary>
    /// Called once the fallback has actually worked with nothing remembered: keeps the server's
    /// authority only if it really did sign the certificate the server presented.
    /// </summary>
    public bool Remember(byte[] authorityData, X509Certificate2 leaf, string httpsAddress)
    {
        if (StorageKey(httpsAddress) is not { } key || ParseCertificate(authorityData) is not { } authority) return false;
        using (authority)
        {
            if (!SignedBy(authority, leaf)) return false;
            _preferences.Set(key, Convert.ToBase64String(authority.RawData));
            return true;
        }
    }

    /// <summary>
    /// The server's certificate authority, from the plain-HTTP address the fallback just reached.
    /// A throwaway client, like <see cref="CheckAsync"/>: nothing of the app's own session goes with it.
    /// </summary>
    public static async Task<byte[]?> FetchAuthorityAsync(string httpAddress, CancellationToken ct = default)
    {
        if (ServerAddress.Normalize(httpAddress) is not { } normalized) return null;
        using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync($"{normalized}/api/certificate-authority", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a certificate presented at <paramref name="requestUri"/> was signed by the authority
    /// remembered for that host and port. For WebView2, whose certificate-error event is the only
    /// say this app has over what its embedded pages trust.
    /// </summary>
    public bool Accepts(Uri requestUri, X509Certificate2 certificate)
    {
        using var authority = RememberedAuthority(requestUri);
        return authority is not null && SignedBy(authority, certificate);
    }

    public void Forget(string httpsAddress)
    {
        if (StorageKey(httpsAddress) is { } key) _preferences.Set(key, "");
    }

    /// <summary>
    /// Chain building against that one authority only. Not the TLS policy: the question is "did our
    /// authority sign this", and the host is already fixed by the address it was fetched from.
    /// </summary>
    internal static bool SignedBy(X509Certificate2 authority, X509Certificate2 leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (!chain.Build(leaf)) return false;
        var root = chain.ChainElements[^1].Certificate;
        return root.RawData.AsSpan().SequenceEqual(authority.RawData);
    }

    /// <summary>The server serves ca.crt as PEM; DER is accepted too.</summary>
    internal static X509Certificate2? ParseCertificate(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : new X509Certificate2(data);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private X509Certificate2? RememberedAuthority(Uri uri)
    {
        var stored = _preferences.GetString(StorageKey(uri));
        if (stored.Length == 0) return null;
        try
        {
            return new X509Certificate2(Convert.FromBase64String(stored));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Per host and port - the same server whichever path or trailing slash the address was saved with.
    private static string? StorageKey(string httpsAddress) =>
        ServerAddress.Normalize(httpsAddress) is { } normalized && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? StorageKey(uri)
            : null;

    private static string StorageKey(Uri uri) => $"{KeyPrefix}{uri.Host.ToLowerInvariant()}:{uri.Port}";
}
