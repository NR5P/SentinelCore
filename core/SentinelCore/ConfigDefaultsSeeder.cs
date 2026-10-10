using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore;

// What happened on this boot, for the one line the call site logs.
public enum ConfigSeedAction
{
    // No appsettings.defaults.json in the directory. The normal result for a build output folder
    // that predates the defaults file, or for the dashboard's folder in a git checkout.
    NotApplicable,
    // Every key in the defaults file was already present. The steady state after the first boot
    // of a given build.
    UpToDate,
    // No appsettings.json existed, so the defaults became it - a fresh release install whose
    // install script didn't already do this.
    Created,
    // At least one key was missing and has been added. The upgrade case this exists for.
    Merged,
    // Something was wrong with one of the files. Deliberately not fatal - see Seed.
    Failed
}

public sealed record ConfigSeedResult(ConfigSeedAction Action, IReadOnlyList<string> AddedKeys, string? Warning)
{
    public static readonly ConfigSeedResult NotApplicable = new(ConfigSeedAction.NotApplicable, [], null);
    public static readonly ConfigSeedResult UpToDate = new(ConfigSeedAction.UpToDate, [], null);

    // One line for the console, or null when there is nothing worth saying. Silence is the
    // common case: most boots are UpToDate, and a message every time would train people to
    // ignore this line on the boot where it actually matters.
    public string? Describe() => Action switch
    {
        ConfigSeedAction.Created => $"Created appsettings.json from {ConfigDefaultsSeeder.DefaultsFileName}.",
        ConfigSeedAction.Merged => $"Added {AddedKeys.Count} new setting(s) introduced by this version, at shipped defaults: {string.Join(", ", AddedKeys)}.",
        ConfigSeedAction.Failed => Warning,
        _ => null
    };
}

// Copies settings that a new version introduced into an existing install's appsettings.json.
//
// Release bundles ship their configuration as appsettings.defaults.json and never as
// appsettings.json - so unpacking a new release over an existing install cannot overwrite the
// live configuration, which is the file the dashboard writes every camera, credential and
// channel setting into. The cost of that safety is that a genuinely new setting in v1.1 would
// otherwise never reach a v1.0 install: its appsettings.json predates the key, and nothing
// would ever add it. This closes that gap on the next boot after an upgrade.
//
// The merge is strictly add-only. A key already present in appsettings.json is never touched,
// whatever its value - including an explicit null, an emptied-out string, or an array the user
// has edited. "The user changed this and we know better" is exactly the behaviour that would
// make an upgrade lose someone's configuration, which is the thing this whole mechanism exists
// to prevent.
public static class ConfigDefaultsSeeder
{
    public const string DefaultsFileName = "appsettings.defaults.json";
    public const string SettingsFileName = "appsettings.json";

    // Same encoder choice as AutoApiKeyProvisioner and AppSettingsFile, for the same reason: the
    // default encoder escapes ordinary characters like apostrophes, so writing this file back
    // would rewrite dozens of untouched PromptProfiles lines and bury the real change.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // `directory` holds both files - the published output folder of one of the two apps. Each
    // app seeds only its own directory: the dashboard writes to the bridge's appsettings.json
    // through AppSettingsFile all the time, and having it also seed that same file at startup
    // would put two processes on one file for no benefit.
    //
    // Nothing here throws. A malformed or unreadable file returns Failed and the app carries on
    // booting with the configuration it already has - that configuration is a working install,
    // and refusing to start over a missing optional default would turn a cosmetic problem into
    // an outage.
    public static ConfigSeedResult Seed(string directory)
    {
        var defaultsPath = Path.Combine(directory, DefaultsFileName);
        var settingsPath = Path.Combine(directory, SettingsFileName);

        if (!File.Exists(defaultsPath)) return ConfigSeedResult.NotApplicable;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(defaultsPath)) is not JsonObject defaults)
                return Failed($"{defaultsPath} does not contain a JSON object at its root - skipping default seeding.");

            if (!File.Exists(settingsPath))
            {
                File.Copy(defaultsPath, settingsPath);
                return new ConfigSeedResult(ConfigSeedAction.Created, [], null);
            }

            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject settings)
                return Failed($"{settingsPath} does not contain a JSON object at its root - skipping default seeding.");

            var added = new List<string>();
            AddMissing(defaults, settings, prefix: "", added);
            if (added.Count == 0) return ConfigSeedResult.UpToDate;

            Write(settingsPath, settings);
            return new ConfigSeedResult(ConfigSeedAction.Merged, added, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failed($"Could not apply new settings from {DefaultsFileName} ({ex.Message}). The existing configuration is unchanged.");
        }
    }

    // Recurses into sections present in both files so that a version adding one key to an
    // existing section (rather than a whole new section) is still picked up. Anything that is
    // not an object on both sides is left alone: replacing a user's array with the shipped one
    // would silently discard edited prompt profiles or camera groups.
    //
    // Key paths are joined with ':' to match how the same setting is named in IConfiguration,
    // logs and the docs, so a seeded key can be searched for as written.
    private static void AddMissing(JsonObject defaults, JsonObject target, string prefix, List<string> added)
    {
        foreach (var (key, defaultValue) in defaults)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}:{key}";

            // Present-but-null counts as present. A user who blanked a value chose that.
            if (!target.TryGetPropertyValue(key, out var existing))
            {
                target[key] = defaultValue?.DeepClone();
                added.Add(path);
                continue;
            }

            if (defaultValue is JsonObject defaultSection && existing is JsonObject targetSection)
                AddMissing(defaultSection, targetSection, path, added);
        }
    }

    private static void Write(string path, JsonObject root)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(WriteOptions));
        AtomicFile.Replace(temporaryPath, path);
    }

    private static ConfigSeedResult Failed(string warning) =>
        new(ConfigSeedAction.Failed, [], warning);
}
