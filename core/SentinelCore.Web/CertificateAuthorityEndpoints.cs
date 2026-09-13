namespace SentinelCore.Web;

// Hands out this install's local certificate authority, so a device that needs to trust the
// dashboard's HTTPS can fetch the certificate from the dashboard itself instead of someone
// copying core/certs/ca.crt off the server by hand (docs/DEPLOYMENT.md section 14a).
//
// Why a device needs it at all: a browser will only hand a page the device's camera over HTTPS
// with a certificate it trusts. The publish page - a laptop or phone becoming one of the cameras -
// is therefore unusable until the authority is trusted on that device: plain HTTP is refused
// outright, and clicking through the certificate warning gets a page whose camera-permission
// request the browser declines without ever showing a prompt. That page is what links here, and
// the device looking at it is by definition the one that needs the file.
//
// Unauthenticated on purpose. A CA certificate is the public half of the pair and exists to be
// distributed; anyone who can reach the HTTPS port is already handed the certificate it signed
// in the TLS handshake. What must never be served is ca.key, and nothing here reads it. The path
// comes from configuration (Program.cs records it when it provisions the authority) rather than
// being recomputed, so an install running on an explicitly configured real-domain certificate,
// which has no local authority, answers 404 instead of guessing at a file.
public static class CertificateAuthorityEndpoints
{
    public const string ConfigurationKey = "Https:AuthorityCertificatePath";

    // Named so a Downloads folder full of "ca.crt" files from different things stays legible.
    public const string DownloadFileName = "sentinelcore-ca.crt";

    // The type that makes macOS open the file in Keychain Access, iOS offer to install a profile
    // and Android recognise it as a CA - rather than displaying the PEM text in a tab.
    public const string ContentType = "application/x-x509-ca-cert";

    public static void MapCertificateAuthorityEndpoints(this WebApplication app)
    {
        // HEAD as well as GET: the publish page asks whether there is anything to download before
        // it offers the link, and a route mapped with MapGet alone answers HEAD with a 405.
        app.MapMethods("/api/certificate-authority", [HttpMethods.Get, HttpMethods.Head], (IConfiguration configuration) =>
        {
            var path = configuration[ConfigurationKey];
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Results.NotFound(new { errors = new[] { "This install has no local certificate authority to download." } });

            return Results.File(path, ContentType, DownloadFileName);
        });
    }
}
