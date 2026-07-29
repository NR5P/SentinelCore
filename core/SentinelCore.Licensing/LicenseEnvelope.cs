namespace SentinelCore.Licensing;

// The on-disk .lic file shape: the signature covers exactly the bytes in Payload (base64 of the
// UTF-8 JSON of a LicenseInfo), so verification never has to guess what was actually signed.
internal sealed record LicenseEnvelope(string Payload, string Signature);
