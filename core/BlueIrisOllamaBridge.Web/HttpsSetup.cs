using System.Security.Cryptography.X509Certificates;

namespace BlueIrisOllamaBridge.Web;

// Optional HTTPS for the dashboard. Off unless a certificate is configured, and even then only
// added alongside the plain HTTP listener rather than replacing it - see docs/DEPLOYMENT.md for
// how to generate a certificate, and why a local authority is needed at all (no public CA will
// issue for a private IP address).
//
// HTTP deliberately keeps listening. The iOS and Android apps both default to http:// when you
// type a bare address, so silently forcing everything to HTTPS would break them the moment a
// device hadn't yet trusted the authority.
public sealed record HttpsSettings(string? CertificatePath, string? KeyPath, int Port)
{
    public static HttpsSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Https");
        return new HttpsSettings(
            section["CertificatePath"],
            section["KeyPath"],
            int.TryParse(section["Port"], out var port) ? port : 3443);
    }

    // Loads the PEM pair into a usable server certificate.
    //
    // Not UseHttps(path, path): that overload's second argument is a PFX *password*, so passing a
    // key path there silently produces a certificate with no private key and Kestrel dies at
    // startup with "server mode SSL must use a certificate with the associated private key".
    // Kestrel's PEM support lives in its configuration binding, not its programmatic overloads.
    public X509Certificate2 LoadCertificate()
    {
        var certificate = X509Certificate2.CreateFromPemFile(CertificatePath!, KeyPath!);

        // Windows can't use the ephemeral key that CreateFromPemFile produces - SslStream needs
        // one that's been through a key store. Round-tripping via PKCS#12 is the standard fix and
        // is a no-op cost on a listener created once at startup.
        if (OperatingSystem.IsWindows())
        {
            var exported = certificate.Export(X509ContentType.Pkcs12);
            certificate.Dispose();
            certificate = X509CertificateLoader.LoadPkcs12(exported, password: null);
        }

        return certificate;
    }

    // Pulls the port back out of the "Urls" setting, because enabling HTTPS forces us to declare
    // every endpoint explicitly and the plain HTTP one has to keep working unchanged. Falls back
    // to the shipped default rather than throwing - a malformed Urls value shouldn't be the
    // thing that stops the dashboard booting.
    public const int DefaultHttpPort = 3320;

    public static int ParseHttpPort(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return DefaultHttpPort;

        // Urls can hold several semicolon-separated addresses; the HTTP one is what we need.
        foreach (var candidate in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            // Uri can't parse the 0.0.0.0/+/* wildcards Kestrel accepts, so read the port off the end.
            var lastColon = candidate.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(candidate[(lastColon + 1)..].TrimEnd('/'), out var port)) return port;
        }

        return DefaultHttpPort;
    }

    // Deliberately requires the files to exist rather than trusting configuration. Kestrel's own
    // certificate config throws at startup when a path is wrong, which would turn a typo - or a
    // certs directory that didn't survive a move - into a dashboard that won't boot at all.
    // Falling back to HTTP with a warning is far better than that: the app stays reachable and
    // the log says exactly what's wrong.
    public bool CanEnable(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(CertificatePath) && string.IsNullOrWhiteSpace(KeyPath))
        {
            reason = null; // Not configured at all - the normal case, nothing to report.
            return false;
        }

        if (string.IsNullOrWhiteSpace(CertificatePath) || string.IsNullOrWhiteSpace(KeyPath))
        {
            reason = "Https needs both CertificatePath and KeyPath - only one was set.";
            return false;
        }

        if (!File.Exists(CertificatePath))
        {
            reason = $"Https certificate not found at {CertificatePath}.";
            return false;
        }

        if (!File.Exists(KeyPath))
        {
            reason = $"Https key not found at {KeyPath}.";
            return false;
        }

        if (Port is < 1 or > 65535)
        {
            reason = $"Https port {Port} is not a usable port number.";
            return false;
        }

        reason = null;
        return true;
    }
}
