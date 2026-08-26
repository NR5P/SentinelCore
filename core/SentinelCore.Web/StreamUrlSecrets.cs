namespace SentinelCore.Web;

// Masking for credentials that live *inside* a camera's stream URL, as opposed to the separate
// Password column that CameraEndpoints.Masked already covers.
//
// Both exist because a camera's credential reaches this system by two routes. A camera configured
// the normal way has its secret in Camera.Password and a clean URL. A camera that cannot use RTSP
// - every Reolink on this network, which is why the HTTP-FLV workaround exists - carries the same
// secret in the URL itself:
//
//   http://host/flv?port=1935&app=bcs&stream=channel0_main.bcs&user=admin&password=<secret>
//   rtsp://admin:<secret>@host/main
//
// Masking only the column left that second copy going out in full from GET /api/cameras, which is
// RequireAuthorization() - any signed-in user, including a Viewer whose whole purpose is to watch
// video and nothing else. So the admin-only /api/cameras/{id}/password endpoint was guarding a door
// with a second one standing open beside it.
//
// Public rather than internal purely for testability - this project has no InternalsVisibleTo,
// the same reason SensorTriggerWatcher's truth table is public.
public static class StreamUrlSecrets
{
    // Query keys whose *value* is a secret. Deliberately does not include "user"/"username":
    // Camera.Username is not masked either, and hiding it would only make a stream URL harder to
    // recognise without protecting anything.
    private static readonly string[] SecretQueryKeys =
        ["password", "pass", "pwd", "token", "secret", "apikey", "api_key", "auth"];

    // Replaces every embedded credential with SettingsSecrets.Mask, leaving the rest of the URL
    // readable - the host, port and path are what makes a stream URL recognisable in a list, and
    // none of them is the secret.
    public static string Mask(string? streamUrl)
    {
        if (string.IsNullOrEmpty(streamUrl)) return streamUrl ?? string.Empty;

        var spans = CredentialSpans(streamUrl);
        if (spans.Count == 0) return streamUrl;

        // Right to left, so an earlier span's offsets stay valid as later ones are replaced.
        var masked = streamUrl;
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var (start, length) = spans[i];
            masked = string.Concat(masked.AsSpan(0, start), SettingsSecrets.Mask, masked.AsSpan(start + length));
        }
        return masked;
    }

    // The other half of the contract, mirroring how CameraEndpoints treats a masked Password on the
    // way back in: an edit form that loaded the masked URL and changed only the host sends the mask
    // straight back, and that means "keep the stored secret", never "set the password to eight
    // bullet characters".
    //
    // Restores positionally rather than by key, so a URL that carries two secrets puts each back
    // where it came from. A mask with no stored counterpart is left as-is: that means the stored URL
    // has no credential there, and writing the bullets in would be worse than leaving them visible.
    public static string Unmask(string? incoming, string? stored)
    {
        if (string.IsNullOrEmpty(incoming)) return incoming ?? string.Empty;
        if (!incoming.Contains(SettingsSecrets.Mask, StringComparison.Ordinal)) return incoming;
        if (string.IsNullOrEmpty(stored)) return incoming;

        var storedSecrets = CredentialSpans(stored)
            .Select(span => stored.Substring(span.Start, span.Length))
            .ToList();
        if (storedSecrets.Count == 0) return incoming;

        var restored = incoming;
        var next = 0;
        // Re-scan each pass: replacing a mask with a secret of a different length moves everything
        // after it, and the secret itself must never be re-examined as if it were a mask.
        while (next < storedSecrets.Count)
        {
            var spans = CredentialSpans(restored);
            var maskIndex = spans.FindIndex(span =>
                restored.AsSpan(span.Start, span.Length).SequenceEqual(SettingsSecrets.Mask));
            if (maskIndex < 0) break;

            var (start, length) = spans[maskIndex];
            restored = string.Concat(
                restored.AsSpan(0, start), storedSecrets[Math.Min(maskIndex, storedSecrets.Count - 1)], restored.AsSpan(start + length));
            next++;
        }
        return restored;
    }

    // Every span of the URL that is a secret's value, in order of appearance.
    private static List<(int Start, int Length)> CredentialSpans(string url)
    {
        var spans = new List<(int Start, int Length)>();

        // userinfo form: scheme://user:secret@host. Bounded to the authority, so a ':' or '@' in a
        // later path or query segment cannot be mistaken for a credential delimiter.
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var authorityStart = schemeEnd + 3;
            var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
            if (authorityEnd < 0) authorityEnd = url.Length;

            if (authorityEnd > authorityStart)
            {
                var at = url.LastIndexOf('@', authorityEnd - 1);
                if (at >= authorityStart)
                {
                    var colon = url.IndexOf(':', authorityStart);
                    // A colon after the '@' is the port, not a password.
                    if (colon > authorityStart && colon < at)
                        spans.Add((colon + 1, at - colon - 1));
                }
            }
        }

        // query form: ?...&password=secret&...
        var queryStart = url.IndexOf('?');
        if (queryStart >= 0)
        {
            var fragment = url.IndexOf('#', queryStart);
            var queryEnd = fragment < 0 ? url.Length : fragment;
            var cursor = queryStart + 1;
            while (cursor < queryEnd)
            {
                var pairEnd = url.IndexOf('&', cursor);
                if (pairEnd < 0 || pairEnd > queryEnd) pairEnd = queryEnd;

                var equals = url.IndexOf('=', cursor);
                if (equals > cursor && equals < pairEnd)
                {
                    var key = url[cursor..equals];
                    if (SecretQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                        spans.Add((equals + 1, pairEnd - equals - 1));
                }
                cursor = pairEnd + 1;
            }
        }

        return spans;
    }
}
