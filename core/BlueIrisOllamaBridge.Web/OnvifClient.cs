using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BlueIrisOllamaBridge.Web;

// WS-Security UsernameToken (the auth scheme ONVIF requires), computed as its own static class
// so the digest math is directly unit-testable without a camera, an HttpClient, or any XML.
//
// PasswordDigest = Base64( SHA-1( nonce_bytes || created_bytes || password_bytes ) ), per the
// OASIS WSS UsernameToken Profile 1.0. The classic implementation bug - and the reason this is
// split out rather than inlined - is hashing the base64 TEXT of the nonce instead of its raw
// bytes: the result is still a plausible-looking digest (right length, valid base64), so it
// doesn't look wrong on inspection. It just fails auth against every real camera. See the tests
// for how each byte-level detail here is pinned down.
public static class OnvifUsernameToken
{
    public sealed record UsernameToken(string Username, string Nonce, string Created, string PasswordDigest);

    // A fresh nonce and timestamp per call, not once per session - some camera firmware keeps a
    // replay cache and silently rejects a second SOAP call that reuses the same nonce.
    public static UsernameToken Build(string username, string password, DateTimeOffset? now = null)
    {
        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var created = (now ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var digest = ComputeDigest(nonceBytes, created, password);
        return new UsernameToken(username, Convert.ToBase64String(nonceBytes), created, Convert.ToBase64String(digest));
    }

    // Public and takes raw nonce bytes directly (rather than only being reachable through the
    // randomized Build above) so tests can pin known nonce/created/password inputs against
    // known digest outputs.
    public static byte[] ComputeDigest(byte[] nonceBytes, string created, string password)
    {
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var toHash = new byte[nonceBytes.Length + createdBytes.Length + passwordBytes.Length];
        nonceBytes.CopyTo(toHash, 0);
        createdBytes.CopyTo(toHash, nonceBytes.Length);
        passwordBytes.CopyTo(toHash, nonceBytes.Length + createdBytes.Length);

#pragma warning disable CA5350 // SHA-1 is mandated by the WS-Security UsernameToken Profile 1.0 that ONVIF requires for this specific digest - it is not a general-purpose security hash and not a choice this code makes.
        return SHA1.HashData(toHash);
#pragma warning restore CA5350
    }
}

// Minimal ONVIF SOAP client: just the four operations needed to turn a discovered device address
// into a real, working RTSP stream URL. Hand-rolled rather than a generated WSDL client - the
// full ONVIF service surface is enormous, and this app needs four operations out of it.
public sealed class OnvifClient(HttpClient httpClient)
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private static readonly XNamespace Tds = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Trt = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace Tt = "http://www.onvif.org/ver10/schema";

    public sealed record DeviceInformation(string Manufacturer, string Model);

    public async Task<DeviceInformation?> GetDeviceInformationAsync(
        string deviceServiceUrl, string? username, string? password, CancellationToken cancellationToken)
    {
        var response = await SendAsync(deviceServiceUrl, new XElement(Tds + "GetDeviceInformation"), username, password, cancellationToken);
        var result = response?.Descendants(Tds + "GetDeviceInformationResponse").FirstOrDefault();
        if (result is null) return null;

        return new DeviceInformation(
            result.Element(Tds + "Manufacturer")?.Value ?? "Unknown",
            result.Element(Tds + "Model")?.Value ?? "Unknown");
    }

    // The device service and the media service (which owns GetProfiles/GetStreamUri) are not
    // guaranteed to be the same endpoint - GetCapabilities is how a client is meant to discover
    // the real media service address rather than guessing a URL path convention.
    public async Task<string?> GetMediaServiceUrlAsync(
        string deviceServiceUrl, string? username, string? password, CancellationToken cancellationToken)
    {
        var body = new XElement(Tds + "GetCapabilities", new XElement(Tds + "Category", "Media"));
        var response = await SendAsync(deviceServiceUrl, body, username, password, cancellationToken);
        return response?.Descendants(Tt + "Media").FirstOrDefault()?.Element(Tt + "XAddr")?.Value;
    }

    public async Task<IReadOnlyList<string>> GetProfileTokensAsync(
        string mediaServiceUrl, string? username, string? password, CancellationToken cancellationToken)
    {
        var response = await SendAsync(mediaServiceUrl, new XElement(Trt + "GetProfiles"), username, password, cancellationToken);
        if (response is null) return [];

        return response.Descendants(Trt + "Profiles")
            .Select(profile => profile.Attribute("token")?.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => token!)
            .ToList();
    }

    public async Task<string?> GetStreamUriAsync(
        string mediaServiceUrl, string profileToken, string? username, string? password, CancellationToken cancellationToken)
    {
        var body = new XElement(Trt + "GetStreamUri",
            new XElement(Trt + "StreamSetup",
                new XElement(Tt + "Stream", "RTP-Unicast"),
                new XElement(Tt + "Transport",
                    new XElement(Tt + "Protocol", "RTSP"))),
            new XElement(Trt + "ProfileToken", profileToken));

        var response = await SendAsync(mediaServiceUrl, body, username, password, cancellationToken);
        return response?.Descendants(Trt + "MediaUri").FirstOrDefault()?.Element(Tt + "Uri")?.Value;
    }

    private async Task<XDocument?> SendAsync(
        string serviceUrl, XElement body, string? username, string? password, CancellationToken cancellationToken)
    {
        var envelope = BuildEnvelope(body, username, password);
        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/soap+xml");

        try
        {
            using var response = await httpClient.PostAsync(serviceUrl, content, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return XDocument.Parse(text);
        }
        // A camera that's unreachable, refuses the connection, times out, or answers with
        // something that isn't valid XML (a login page, a 404 body) is not exceptional here -
        // it just means this credential/endpoint combination didn't work, which the caller
        // handles by trying the next fallback.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    // Public so tests can check the envelope/header shape without a network call.
    public static XElement BuildEnvelope(XElement body, string? username, string? password)
    {
        var header = new XElement(Soap + "Header");
        if (!string.IsNullOrWhiteSpace(username))
        {
            var token = OnvifUsernameToken.Build(username, password ?? string.Empty);
            header.Add(new XElement(Wsse + "Security",
                new XElement(Wsse + "UsernameToken",
                    new XElement(Wsse + "Username", token.Username),
                    new XElement(Wsse + "Password",
                        new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"),
                        token.PasswordDigest),
                    new XElement(Wsse + "Nonce",
                        new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"),
                        token.Nonce),
                    new XElement(Wsu + "Created", token.Created))));
        }

        return new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wsse", Wsse.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wsu", Wsu.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "tds", Tds.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "trt", Trt.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "tt", Tt.NamespaceName),
            header,
            new XElement(Soap + "Body", body));
    }
}
