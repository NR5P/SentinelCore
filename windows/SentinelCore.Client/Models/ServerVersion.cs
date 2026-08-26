namespace SentinelCore.Client.Models;

// The server's build identity as /api/health reports it - see BuildInfo.cs, which produces these
// exact field names. That endpoint is deliberately unauthenticated, so this works even while the
// app is sitting at the login screen.
public sealed class ServerBuild
{
    public string Version { get; init; } = "";
    public string Commit { get; init; } = "";
    public bool IsDirty { get; init; }
    public string Display { get; init; } = "";
}

// Just the numeric major.minor.patch, which is all this app ever needs to order two builds. SemVer
// pre-release and build metadata ("1.2.0-beta.1", "1.0.0+ab12cd.dirty") are dropped rather than
// parsed: the commit is already reported separately, and nothing here should refuse to compare two
// versions because one of them was built from a dirty tree.
public readonly record struct ServerVersion(int Major, int Minor, int Patch) : IComparable<ServerVersion>
{
    // The oldest server this app is known to work against. Bump this in the same change that
    // starts relying on a server feature the previous release didn't have - that is the entire
    // point of it, and a bump with no such dependency will only nag people for no reason.
    public static ServerVersion MinimumSupported { get; } = new(1, 0, 0);

    public static ServerVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var numeric = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major)) return null;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return new ServerVersion(major, minor, patch);
    }

    public int CompareTo(ServerVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ServerVersion left, ServerVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ServerVersion left, ServerVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ServerVersion left, ServerVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ServerVersion left, ServerVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public enum ServerCompatibilityKind
{
    Ok,

    // Reported a version older than MinimumSupported.
    Outdated,

    // Answered, but reported no build at all - older than version reporting itself, so older than
    // anything this app has been built against.
    PredatesVersionReporting,

    // /api/health didn't answer, or didn't parse. Says nothing conclusive about the server's
    // version, so it stays silent rather than guessing - a transient blip on a working connection
    // must not raise a scary banner.
    Unknown,
}

// Advisory only, deliberately. A version mismatch between an app and a server on the same LAN is
// usually survivable - most endpoints don't change between releases - and an app that refuses to
// start is far more disruptive than a slightly wrong screen. So this never blocks: it explains
// what's mismatched and leaves the decision to the person at the keyboard.
public readonly record struct ServerCompatibility(ServerCompatibilityKind Kind, string ServerVersionText)
{
    public static ServerCompatibility Unknown { get; } = new(ServerCompatibilityKind.Unknown, "");

    public static ServerCompatibility Evaluate(ServerBuild? build)
    {
        if (build is null) return new ServerCompatibility(ServerCompatibilityKind.PredatesVersionReporting, "");
        if (ServerVersion.Parse(build.Version) is not { } version) return Unknown;
        return version < ServerVersion.MinimumSupported
            ? new ServerCompatibility(ServerCompatibilityKind.Outdated, build.Version)
            : new ServerCompatibility(ServerCompatibilityKind.Ok, build.Version);
    }

    // Null when there is nothing worth telling the user, which is the overwhelmingly common case.
    public string? Advisory => Kind switch
    {
        ServerCompatibilityKind.Outdated =>
            $"This server is running version {ServerVersionText}, older than the {ServerVersion.MinimumSupported} " +
            "this app expects. Most things still work, but some screens may be wrong or empty until the server is updated.",
        ServerCompatibilityKind.PredatesVersionReporting =>
            "This server is too old to report its version, which means it predates several features this app uses. " +
            "Most things still work, but some screens may be wrong or empty until the server is updated.",
        _ => null,
    };
}
