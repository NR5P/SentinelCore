using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SentinelCore.Web;

// Generates the local certificate authority + server certificate that HttpsSettings needs,
// entirely in-process - nobody has to install openssl, run a script, or hand-edit
// appsettings.json to get HTTPS working. See docs/DEPLOYMENT.md section 14a for why a *local*
// authority is unavoidable at all: no public CA will issue for a private LAN address.
//
// Safe to call on every startup. It reuses an existing authority (so devices that already
// trust it stay trusted) and only reissues the server certificate when it's missing or close
// to expiring - matching what the old setup-local-https.sh script plus its systemd renewal
// timer did by hand, but now automatic and requiring no external tools.
public static class AutoCertificateProvisioner
{
    private const int AuthorityValidityDays = 3650;

    // Apple caps certificates at 825 days for every CA, including ones added by hand - going
    // over this makes iPhones reject the certificate no matter how correctly it's trusted.
    private const int ServerValidityDays = 825;

    private const int RenewWithinDays = 30;

    public sealed record Result(string CertificatePath, string KeyPath, string AuthorityCertificatePath, bool Reissued);

    public static Result EnsureCertificate(string certDirectory)
    {
        Directory.CreateDirectory(certDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(certDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var authorityCertPath = Path.Combine(certDirectory, "ca.crt");
        var authorityKeyPath = Path.Combine(certDirectory, "ca.key");
        var serverCertPath = Path.Combine(certDirectory, "server.crt");
        var serverKeyPath = Path.Combine(certDirectory, "server.key");

        using var authorityKey = RSA.Create();
        using var authorityCertificate = LoadOrCreateAuthority(authorityCertPath, authorityKeyPath, authorityKey);

        if (!NeedsReissue(serverCertPath))
            return new Result(serverCertPath, serverKeyPath, authorityCertPath, Reissued: false);

        IssueServerCertificate(authorityCertificate, authorityKey, serverCertPath, serverKeyPath);
        return new Result(serverCertPath, serverKeyPath, authorityCertPath, Reissued: true);
    }

    // Returns the authority certificate; populates `authorityKey` (created by the caller) with
    // its private key either way, so the two are always a matched pair regardless of whether
    // this run created them or loaded them from a previous one.
    private static X509Certificate2 LoadOrCreateAuthority(string certPath, string keyPath, RSA authorityKey)
    {
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            authorityKey.ImportFromPem(File.ReadAllText(keyPath));
            return X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
        }

        authorityKey.KeySize = 4096;
        var subject = new X500DistinguishedName($"CN=Sentinel Core Local CA ({SafeHostName()}), O=Sentinel Core");
        var request = new CertificateRequest(subject, authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var authorityCertificate = request.CreateSelfSigned(notBefore, notBefore.AddDays(AuthorityValidityDays));

        File.WriteAllText(certPath, authorityCertificate.ExportCertificatePem());
        File.WriteAllText(keyPath, authorityKey.ExportPkcs8PrivateKeyPem());
        TightenKeyPermissions(keyPath);

        return authorityCertificate;
    }

    private static bool NeedsReissue(string certPath)
    {
        if (!File.Exists(certPath)) return true;
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
        return certificate.NotAfter <= DateTime.UtcNow.AddDays(RenewWithinDays);
    }

    private static void IssueServerCertificate(X509Certificate2 authorityCertificate, RSA authorityKey, string certPath, string keyPath)
    {
        using var serverKey = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={SafeHostName()}.local, O=Sentinel Core");
        var request = new CertificateRequest(subject, serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: true)); // serverAuth

        // Browsers ignore the Common Name and match only the Subject Alternative Names - IP
        // addresses need explicit AddIpAddress entries, listing one as a DNS name silently fails
        // to match. Covers this machine's hostname, its .local name, localhost, and every LAN IP
        // it currently has, the same set the old setup-local-https.sh script covered by hand.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in DiscoverLocalDnsNames()) sanBuilder.AddDnsName(dnsName);
        foreach (var ipAddress in DiscoverLocalIpAddresses()) sanBuilder.AddIpAddress(ipAddress);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddDays(ServerValidityDays);
        if (notAfter > authorityCertificate.NotAfter) notAfter = authorityCertificate.NotAfter; // a leaf can't outlive its issuer

        // Signs with the authority's raw key rather than requiring an X509Certificate2 with an
        // attached private key - works identically whether the authority was just created above
        // or loaded back from disk via ImportFromPem, which has no such attachment.
        var generator = X509SignatureGenerator.CreateForRSA(authorityKey, RSASignaturePadding.Pkcs1);
        using var signed = request.Create(authorityCertificate.SubjectName, generator, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));

        File.WriteAllText(certPath, signed.ExportCertificatePem());
        File.WriteAllText(keyPath, serverKey.ExportPkcs8PrivateKeyPem());
        TightenKeyPermissions(keyPath);
    }

    private static void TightenKeyPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return; // ACLs work differently; the directory itself is already user-only
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string SafeHostName() => Dns.GetHostName().Split('.')[0];

    private static IEnumerable<string> DiscoverLocalDnsNames()
    {
        yield return "localhost";
        var shortHost = SafeHostName();
        yield return shortHost;
        yield return $"{shortHost}.local";
    }

    private static IEnumerable<IPAddress> DiscoverLocalIpAddresses()
    {
        yield return IPAddress.Loopback;
        yield return IPAddress.IPv6Loopback;

        IEnumerable<NetworkInterface> interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break; // Best-effort - localhost/127.0.0.1 above still make the certificate usable.
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    yield return unicast.Address;
            }
        }
    }
}
