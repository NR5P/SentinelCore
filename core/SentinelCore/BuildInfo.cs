using System.Reflection;
using System.Text.Json.Serialization;

namespace SentinelCore;

// The build identity of the running process, read back out of the AssemblyInformationalVersion
// that Directory.Build.props stamps at compile time ("1.0.0+<sha>" / "1.0.0+<sha>.dirty").
//
// The point of this existing at all: when a tester says "the live grid is broken", the version
// and commit are the difference between diagnosing it and guessing. So the same value is
// surfaced everywhere a report can originate - the health endpoints, the dashboard footer, and
// every bug report email.
public sealed record BuildVersion(string Version, string Commit, bool IsDirty)
{
    // What a build with no git information available reports - see the .git guard in
    // Directory.Build.props for when that happens (source tarballs, mainly).
    public const string UnknownCommit = "unknown";

    // Internal branching only - the serialized form (this record is returned directly by the
    // health endpoints) is version/commit/isDirty/display, and a redundant flag on top of those
    // would just be one more thing for a client to disagree with us about.
    [JsonIgnore]
    public bool HasCommit => Commit != UnknownCommit;

    // The release build number auto-update compares - stamped only into bundles built by
    // scripts/publish-update.sh (see Directory.Build.props). 0 everywhere else: a git checkout,
    // dotnet run, or a test host.
    public int Build { get; init; }

    // Single human-readable string for the places that want one line rather than fields.
    public string Display => (HasCommit, IsDirty) switch
    {
        (false, _) => $"{Version} (commit unknown)",
        (true, true) => $"{Version} ({Commit}, modified)",
        (true, false) => $"{Version} ({Commit})"
    };

    // Splits SemVer build metadata off the version: "1.0.0+abc123.dirty" -> 1.0.0 / abc123 /
    // dirty. Anything unparseable degrades to "unknown commit" rather than throwing - a
    // diagnostic aid must never be the reason a process fails to start.
    public static BuildVersion Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return new BuildVersion("0.0.0", UnknownCommit, IsDirty: false);

        var parts = informationalVersion.Split('+', 2);
        var version = parts[0].Trim();
        if (version.Length == 0) version = "0.0.0";

        if (parts.Length == 1)
            return new BuildVersion(version, UnknownCommit, IsDirty: false);

        var metadata = parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var isDirty = metadata.Contains("dirty", StringComparer.OrdinalIgnoreCase);
        var commit = metadata.FirstOrDefault(part => !part.Equals("dirty", StringComparison.OrdinalIgnoreCase));

        return new BuildVersion(version, string.IsNullOrWhiteSpace(commit) ? UnknownCommit : commit, isDirty);
    }
}

public static class BuildInfo
{
    // Entry assembly, not this one: the bridge and the dashboard are separate processes that can
    // be updated independently, and each should report the version of the code it is actually
    // running rather than the version of the shared library it happens to reference. Falls back
    // to this assembly under a test host, where there is no meaningful entry assembly.
    public static BuildVersion Current { get; } = Read(Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly);

    private static BuildVersion Read(Assembly assembly) =>
        BuildVersion.Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) with
        {
            Build = ParseBuildNumber(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BuildNumber")?.Value)
        };

    public static int ParseBuildNumber(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var build) && build > 0
            ? build
            : 0;
}
