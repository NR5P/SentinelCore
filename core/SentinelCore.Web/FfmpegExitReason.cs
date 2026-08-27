namespace SentinelCore.Web;

// What ffmpeg's stderr says about why a detection session died.
//
// TriggerZoneMonitor used to drain that stream into an empty loop, which cost two things. The
// operator lost every diagnostic: a camera that fell off the network produced "had exited; starting
// a fresh session" every ten seconds for nine hours and never once said why, while the answer -
// "No route to host" - was being read and discarded on each of those attempts.
//
// The second cost is a wrong decision. A session that exits without ever delivering a frame was
// treated as proof that the camera's derived low-resolution substream does not exist, latching that
// camera onto its main stream for the life of the process. Measured against the actual cameras on
// this network, that inference is backwards:
//
//   substream genuinely absent   ffmpeg BLOCKS - the camera accepts the connection and never
//                                answers, so the process stays alive and the stall watchdog owns it
//   camera unreachable           ffmpeg EXITS immediately, "No route to host"
//   port closed                  ffmpeg EXITS immediately, "Connection refused"
//   wrong password over HTTP     ffmpeg EXITS immediately, "End of file"
//   wrong password over RTSP     ffmpeg EXITS immediately, "401 Unauthorized"
//
// So on this hardware an immediate exit is almost never evidence about the substream, and the one
// latch keyed on it fires only on false positives. Rather than guess from the exit alone, read what
// ffmpeg actually said.
public static class FfmpegExitReason
{
    // How many trailing stderr lines a session keeps. ffmpeg puts the cause in its last handful of
    // lines and everything before that is banner and stream-mapping detail, so a short tail carries
    // the whole diagnosis at a fixed, tiny cost per session.
    public const int LinesKept = 10;

    // The source could not be reached at all - nothing answered, so nothing was learned about what
    // it would have served. Distinct from "the server answered and refused this stream".
    private static readonly string[] UnreachableSignatures =
    [
        "No route to host",
        "Connection refused",
        "Connection timed out",
        "Network is unreachable",
        "Host is unreachable",
        "Host is down",
        "Failed to resolve hostname",
        "Name or service not known",
        "Temporary failure in name resolution",
    ];

    // The server answered and refused the credentials. Deliberately checked before - and excluded
    // from - StreamRejectedSignatures below, even though a 401 is literally a 4xx and "Server
    // returned 4" would otherwise swallow it. Reading an auth failure as "this rendition does not
    // exist" is how a blanked password came to look like a camera with no substream: on 2026-08-27
    // the gate camera lost its login, answered every DESCRIBE with 401, and was promoted onto its
    // 4K main stream for ten minutes at a time by exactly that misreading.
    private static readonly string[] AuthenticationSignatures =
    [
        // Both spellings: ffmpeg's RTSP demuxer prints "method DESCRIBE failed: 401 Unauthorized",
        // and the line it derives from that omits the space.
        "Unauthorized",
        "authorization failed",
        "403 Forbidden",
    ];

    // The server answered and would not serve this particular stream. Only these justify concluding
    // anything about whether a substream exists, because only these mean something was there to
    // answer the question.
    private static readonly string[] StreamRejectedSignatures =
    [
        "404 Not Found",
        "400 Bad Request",
        "Server returned 4",
        "Server returned 5",
        "Stream not found",
        "Invalid data found when processing input",
        "does not contain any stream",
    ];

    public static bool IndicatesSourceUnreachable(IEnumerable<string> stderr) =>
        MatchesAny(stderr, UnreachableSignatures);

    // The credentials were rejected. Says nothing about the rendition, and nothing that trying a
    // different one would fix: every rendition on a camera is reached with the same login.
    public static bool IndicatesAuthenticationFailure(IEnumerable<string> stderr) =>
        MatchesAny(stderr, AuthenticationSignatures);

    // Deliberately requires positive evidence rather than defaulting to true on an uninformative
    // exit. Latching wrongly is the expensive direction: it condemns a working camera to decoding
    // its main stream at full rate for the life of the process, which is the standing-latency
    // failure that made triggers save empty images. Declining to latch when the reason is unclear
    // costs at most a few more restart attempts, and those are now backed off.
    public static bool IndicatesSubstreamMissing(IEnumerable<string> stderr)
    {
        // Materialised because it is read twice, and callers pass queries over a live log buffer.
        var lines = stderr as IReadOnlyCollection<string> ?? stderr.ToList();

        // A refused login is not an answer to the question this asks, so it disqualifies the
        // evidence rather than counting as some of it.
        return !MatchesAny(lines, AuthenticationSignatures) && MatchesAny(lines, StreamRejectedSignatures);
    }

    private static bool MatchesAny(IEnumerable<string> stderr, string[] signatures) =>
        stderr.Any(line => signatures.Any(signature => line.Contains(signature, StringComparison.OrdinalIgnoreCase)));

    // The one line worth putting in the log, with ffmpeg's noise removed and any credential masked.
    //
    // Masking is not optional here. ffmpeg echoes the input URL back in its error text, and on every
    // Reolink on this network that URL carries the admin password in a query parameter - so logging
    // the raw line would write the camera's password into the journal, undoing StreamUrlSecrets for
    // the sake of a diagnostic.
    public static string? Describe(IEnumerable<string> stderr)
    {
        var lines = stderr.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0) return null;

        // "Error opening input: X" names the cause; the "Error opening input file <url>" line that
        // follows it is mostly URL. Prefer the former, fall back to the last line, which is where
        // ffmpeg puts its final word.
        var best = lines.LastOrDefault(line => line.Contains("Error opening input:", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault(line => UnreachableSignatures
                .Concat(StreamRejectedSignatures).Concat(AuthenticationSignatures)
                .Any(signature => line.Contains(signature, StringComparison.OrdinalIgnoreCase)))
            ?? lines[^1];

        return StreamUrlSecrets.Mask(StripComponentPrefix(best));
    }

    // ffmpeg prefixes most lines with "[tcp @ 0x61ac55431040] ". The component name is occasionally
    // useful and the heap address never is, but neither survives being read at a glance, so the
    // whole prefix goes.
    private static string StripComponentPrefix(string line)
    {
        if (!line.StartsWith('[')) return line;

        var close = line.IndexOf(']');
        if (close < 0 || close == line.Length - 1) return line;

        // Only strip the "@ 0x..." shape, so a message that legitimately opens with a bracket is
        // left intact.
        if (!line.AsSpan(0, close).Contains("@ 0x", StringComparison.Ordinal)) return line;

        return line[(close + 1)..].TrimStart();
    }
}
