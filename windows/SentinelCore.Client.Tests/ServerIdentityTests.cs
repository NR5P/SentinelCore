using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SentinelCore.Client;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// The trust-on-first-use check that decides whether the plain-HTTP fallback may carry the session
/// cookie and the saved password. Real certificates and a real TLS handshake throughout: the point
/// is what the chain builder and the handshake actually accept.
/// </summary>
public class ServerIdentityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N"));

    private ServerIdentity NewIdentity()
    {
        Directory.CreateDirectory(_directory);
        return new ServerIdentity(new PreferenceStore(Path.Combine(_directory, "settings.json")));
    }

    [Fact]
    public void Only_the_authority_that_issued_a_certificate_signs_it()
    {
        using var authority = NewAuthority("Sentinel Core Local CA");
        using var other = NewAuthority("Sentinel Core Local CA");
        using var leaf = NewLeaf(authority, "127.0.0.1");
        using var selfSigned = NewAuthority("127.0.0.1");

        Assert.True(ServerIdentity.SignedBy(authority, leaf));
        // Same name, different key: a reinstalled server, or an impostor that copied the name.
        Assert.False(ServerIdentity.SignedBy(other, leaf));
        Assert.False(ServerIdentity.SignedBy(authority, selfSigned));
    }

    [Fact]
    public void An_authority_is_remembered_only_if_it_signed_the_certificate_the_server_showed()
    {
        var identity = NewIdentity();
        using var authority = NewAuthority("Sentinel Core Local CA");
        using var other = NewAuthority("Sentinel Core Local CA");
        using var leaf = NewLeaf(authority, "127.0.0.1");
        var address = new Uri("https://192.168.1.50:3443/api/health");

        Assert.False(identity.Remember(Pem(other), leaf, "https://192.168.1.50:3443"));
        Assert.False(identity.Accepts(address, leaf));

        // Served as PEM, the way /api/certificate-authority does.
        Assert.True(identity.Remember(Pem(authority), leaf, "https://192.168.1.50:3443/"));
        Assert.True(identity.Accepts(address, leaf));
        // Per host and port: another server on the same LAN gets nothing from this one's authority.
        Assert.False(identity.Accepts(new Uri("https://192.168.1.51:3443/"), leaf));

        // A server reissues its certificate, with a new key, when its IP changes; still accepted.
        using var reissued = NewLeaf(authority, "192.168.1.60");
        Assert.True(identity.Accepts(address, reissued));

        identity.Forget("https://192.168.1.50:3443");
        Assert.False(identity.Accepts(address, leaf));
    }

    [Fact]
    public async Task The_check_tells_a_first_visit_from_this_server_from_an_impostor()
    {
        var identity = NewIdentity();
        using var authority = NewAuthority("Sentinel Core Local CA");
        using var impostorAuthority = NewAuthority("Sentinel Core Local CA");
        using var leaf = NewLeaf(authority, "127.0.0.1");
        using var impostorLeaf = NewLeaf(impostorAuthority, "127.0.0.1");
        var port = FreePort();

        using (var server = new TlsServer(leaf, port))
        {
            var first = await identity.CheckAsync(server.Address);
            Assert.Equal(IdentityVerdict.Unknown, first.Verdict);
            Assert.Equal(leaf.Thumbprint, first.Leaf?.Thumbprint);
            // Nothing remembered means the handshake is abandoned: not even the request goes.
            Assert.Equal(0, server.RequestsAnswered);

            Assert.True(identity.Remember(Pem(authority), first.Leaf!, server.Address));
            var again = await identity.CheckAsync(server.Address);
            Assert.Equal(IdentityVerdict.Verified, again.Verdict);
            Assert.Equal(1, server.RequestsAnswered);
        }

        // Something else answering at the very same address, with its own authority.
        using var impostor = new TlsServer(impostorLeaf, port);
        var verdict = await identity.CheckAsync(impostor.Address);
        Assert.Equal(IdentityVerdict.Mismatch, verdict.Verdict);
        Assert.Equal(0, impostor.RequestsAnswered);
    }

    [Fact]
    public async Task A_certificate_seen_earlier_counts_only_when_the_server_completes_the_handshake_with_it()
    {
        var identity = NewIdentity();
        using var authority = NewAuthority("Sentinel Core Local CA");
        using var leaf = NewLeaf(authority, "127.0.0.1");
        using var server = new TlsServer(leaf);

        var seen = await identity.CheckAsync(server.Address, knownLeafThumbprint: leaf.Thumbprint);
        Assert.Equal(IdentityVerdict.Unknown, seen.Verdict);
        Assert.True(seen.Completed);

        using var other = NewLeaf(authority, "127.0.0.1");
        var different = await identity.CheckAsync(server.Address, knownLeafThumbprint: other.Thumbprint);
        Assert.False(different.Completed);
        Assert.Equal(1, server.RequestsAnswered);
    }

    [Fact]
    public async Task Nothing_answering_is_not_mistaken_for_a_verdict()
    {
        var identity = NewIdentity();
        var port = FreePort();

        var check = await identity.CheckAsync($"https://127.0.0.1:{port}");

        Assert.Equal(IdentityVerdict.Unavailable, check.Verdict);
        Assert.Equal(IdentityVerdict.Unavailable, (await identity.CheckAsync("http://127.0.0.1:3320")).Verdict);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static X509Certificate2 NewAuthority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Reload(created);
    }

    private static X509Certificate2 NewLeaf(X509Certificate2 authority, string ip)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Sentinel Core", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(ip));
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var issued = request.Create(
            authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return Reload(withKey);
    }

    // An ephemeral key can't serve a TLS handshake on every platform; a PFX round trip makes it a
    // persisted one that can.
    private static X509Certificate2 Reload(X509Certificate2 certificate) =>
        new(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

    private static byte[] Pem(X509Certificate2 certificate) =>
        Encoding.ASCII.GetBytes(new string(PemEncoding.Write("CERTIFICATE", certificate.RawData)));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The smallest HTTPS server that answers one request per connection with 200.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private int _answered;

        public TlsServer(X509Certificate2 certificate, int port = 0)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Address = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(ServeAsync);
        }

        public string Address { get; }

        public int RequestsAnswered => Volatile.Read(ref _answered);

        private async Task ServeAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(_certificate);
                            var buffer = new byte[4096];
                            var received = new StringBuilder();
                            while (!received.ToString().Contains("\r\n\r\n"))
                            {
                                var read = await tls.ReadAsync(buffer);
                                if (read == 0) return;
                                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            }
                            Interlocked.Increment(ref _answered);
                            await tls.WriteAsync(Encoding.ASCII.GetBytes(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"));
                        }
                        catch (Exception)
                        {
                            // The client abandoning the handshake is the expected outcome for any
                            // certificate it won't accept.
                        }
                    }
                });
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
