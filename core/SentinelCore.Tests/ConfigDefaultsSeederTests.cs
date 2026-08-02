using System.Text.Json.Nodes;
using Xunit;

namespace SentinelCore.Tests;

// The seeder is what lets a release .zip be unpacked over an existing install without taking
// that install's configuration with it. Everything here is really one question asked from
// several angles: does an upgrade ever lose a setting the user chose? A regression in this file
// would not be visible on a fresh install, would not fail any other test, and would show up as
// a tester's cameras and API keys quietly reverting to defaults after an update.
public sealed class ConfigDefaultsSeederTests : IDisposable
{
    private readonly string _directory;

    public ConfigDefaultsSeederTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"config-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteDefaults(string json) =>
        File.WriteAllText(Path.Combine(_directory, ConfigDefaultsSeeder.DefaultsFileName), json);

    private void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(_directory, ConfigDefaultsSeeder.SettingsFileName), json);

    private JsonObject ReadSettings() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, ConfigDefaultsSeeder.SettingsFileName)))!;

    private ConfigSeedResult Seed() => ConfigDefaultsSeeder.Seed(_directory);

    // The reason the whole mechanism exists: v1.1 adds a setting, and an appsettings.json written
    // by v1.0 has never heard of it.
    [Fact]
    public void AddsSettingsIntroducedByANewerVersion()
    {
        WriteDefaults("""{ "Existing": { "Value": 1 }, "NewFeature": { "Enabled": false, "Mode": "auto" } }""");
        WriteSettings("""{ "Existing": { "Value": 1 } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.Merged, result.Action);
        Assert.Equal(["NewFeature"], result.AddedKeys);
        var settings = ReadSettings();
        Assert.Equal("auto", (string?)settings["NewFeature"]!["Mode"]);
    }

    // The single most important behaviour here. Every one of these is a value someone typed into
    // the dashboard, and the shipped default for all of them is empty or off - so "defaults win"
    // would read as the upgrade having wiped their configuration.
    [Fact]
    public void NeverOverwritesAValueThatIsAlreadySet()
    {
        WriteDefaults("""
            { "Bridge": { "ApiKey": "" }, "Telegram": { "Enabled": false, "BotToken": "" } }
            """);
        WriteSettings("""
            { "Bridge": { "ApiKey": "live-key" }, "Telegram": { "Enabled": true, "BotToken": "live-token" } }
            """);

        var result = Seed();

        Assert.Equal(ConfigSeedAction.UpToDate, result.Action);
        var settings = ReadSettings();
        Assert.Equal("live-key", (string?)settings["Bridge"]!["ApiKey"]);
        Assert.True((bool)settings["Telegram"]!["Enabled"]!);
        Assert.Equal("live-token", (string?)settings["Telegram"]!["BotToken"]);
    }

    // A new key added to a section that already exists - the common shape of a real change, and
    // the case a top-level-only merge would miss.
    [Fact]
    public void MergesIntoAnExistingSectionKeyByKey()
    {
        WriteDefaults("""{ "Telegram": { "Enabled": false, "BotToken": "", "ThreadId": "" } }""");
        WriteSettings("""{ "Telegram": { "Enabled": true, "BotToken": "live-token" } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.Merged, result.Action);
        Assert.Equal(["Telegram:ThreadId"], result.AddedKeys);
        var telegram = ReadSettings()["Telegram"]!;
        Assert.True((bool)telegram["Enabled"]!);
        Assert.Equal("live-token", (string?)telegram["BotToken"]);
        Assert.Equal("", (string?)telegram["ThreadId"]);
    }

    // Arrays are replaced wholesale or not at all, and "not at all" is the only safe choice: the
    // shipped PromptProfiles list would otherwise overwrite a user's edited prompts, and there is
    // no way to tell an intentionally shortened list from an out-of-date one.
    [Fact]
    public void LeavesAnEditedArrayAlone()
    {
        WriteDefaults("""{ "PromptProfiles": ["shipped-one", "shipped-two"] }""");
        WriteSettings("""{ "PromptProfiles": ["my-own"] }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.UpToDate, result.Action);
        var profiles = ReadSettings()["PromptProfiles"]!.AsArray();
        Assert.Equal(["my-own"], profiles.Select(node => (string?)node));
    }

    // Blanking a value in the dashboard writes null or "" rather than removing the key. Treating
    // that as "missing" would hand the setting straight back on the next restart, which from the
    // user's side is the setting refusing to turn off.
    [Fact]
    public void TreatsAnExplicitNullAsAChoiceRatherThanAGap()
    {
        WriteDefaults("""{ "Webhook": { "Url": "https://example.invalid/hook" } }""");
        WriteSettings("""{ "Webhook": { "Url": null } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.UpToDate, result.Action);
        Assert.Null((string?)ReadSettings()["Webhook"]!["Url"]);
    }

    // A type that changed shape between versions. Overwriting would arguably be "correct", but a
    // startup path is the wrong place to be clever - leave it and let validation report it.
    [Fact]
    public void LeavesAValueWhoseTypeDisagreesWithTheDefault()
    {
        WriteDefaults("""{ "Ollama": { "Model": { "Name": "gemma3:4b" } } }""");
        WriteSettings("""{ "Ollama": { "Model": "gemma3:4b" } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.UpToDate, result.Action);
        Assert.Equal("gemma3:4b", (string?)ReadSettings()["Ollama"]!["Model"]);
    }

    // A git-checkout install: appsettings.json is the checked-in file and no defaults ship
    // alongside it. Must be a no-op rather than an error.
    [Fact]
    public void DoesNothingWhenNoDefaultsFileShipped()
    {
        WriteSettings("""{ "Bridge": { "ApiKey": "live-key" } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.NotApplicable, result.Action);
        Assert.Null(result.Describe());
        Assert.Equal("live-key", (string?)ReadSettings()["Bridge"]!["ApiKey"]);
    }

    // A fresh release install where the install script hasn't created appsettings.json - belt and
    // braces so the app still starts if someone runs the binary directly out of the .zip.
    [Fact]
    public void CreatesTheSettingsFileWhenThereIsNone()
    {
        WriteDefaults("""{ "Bridge": { "ApiKey": "" }, "Setup": { "Completed": false } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.Created, result.Action);
        Assert.False((bool)ReadSettings()["Setup"]!["Completed"]!);
    }

    // Hand-edited JSON with a trailing comma is a genuinely common way to arrive here. Refusing
    // to start would turn a typo into an outage on a machine whose whole job is to be watching.
    [Fact]
    public void ReportsRatherThanThrowsOnUnparseableSettings()
    {
        WriteDefaults("""{ "NewFeature": { "Enabled": false } }""");
        WriteSettings("{ \"Bridge\": { \"ApiKey\": \"live-key\", } }");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.Failed, result.Action);
        Assert.NotNull(result.Describe());
        // Left exactly as the user wrote it - a half-repaired config file would be worse than the
        // typo, and they need to recognise what they are fixing.
        Assert.Contains("live-key", File.ReadAllText(Path.Combine(_directory, ConfigDefaultsSeeder.SettingsFileName)));
    }

    [Fact]
    public void ReportsRatherThanThrowsOnUnparseableDefaults()
    {
        WriteDefaults("not json at all");
        WriteSettings("""{ "Bridge": { "ApiKey": "live-key" } }""");

        var result = Seed();

        Assert.Equal(ConfigSeedAction.Failed, result.Action);
        Assert.Equal("live-key", (string?)ReadSettings()["Bridge"]!["ApiKey"]);
    }

    // Every boot after the first one for a given build takes this path, so it must not rewrite
    // the file - a settings file whose timestamp changes on every restart makes it impossible to
    // tell when configuration was last genuinely edited.
    [Fact]
    public void LeavesTheFileUntouchedWhenNothingIsMissing()
    {
        WriteDefaults("""{ "Bridge": { "ApiKey": "" } }""");
        WriteSettings("""{ "Bridge": { "ApiKey": "live-key" }, "Extra": "kept" }""");
        var settingsPath = Path.Combine(_directory, ConfigDefaultsSeeder.SettingsFileName);
        var before = File.ReadAllText(settingsPath);

        var result = Seed();

        Assert.Equal(ConfigSeedAction.UpToDate, result.Action);
        Assert.Equal(before, File.ReadAllText(settingsPath));
    }

    // Settings the defaults have never heard of - anything the user added by hand, or that an
    // older version wrote and this one dropped - survive. This is add-only in both directions.
    [Fact]
    public void KeepsSettingsThatAreNotInTheDefaultsAtAll()
    {
        WriteDefaults("""{ "Known": { "A": 1 } }""");
        WriteSettings("""{ "Known": { "A": 1, "HandAdded": "keep me" }, "Retired": { "B": 2 } }""");

        Seed();

        var settings = ReadSettings();
        Assert.Equal("keep me", (string?)settings["Known"]!["HandAdded"]);
        Assert.Equal(2, (int)settings["Retired"]!["B"]!);
    }

    // Nested paths are reported the way the same setting is named everywhere else - in
    // IConfiguration, in the logs and in DEPLOYMENT.md - so a seeded key can be searched for.
    [Fact]
    public void ReportsAddedKeysAsConfigurationPaths()
    {
        WriteDefaults("""{ "A": { "B": { "C": 1, "D": 2 } } }""");
        WriteSettings("""{ "A": { "B": { "C": 1 } } }""");

        var result = Seed();

        Assert.Equal(["A:B:D"], result.AddedKeys);
        Assert.Contains("A:B:D", result.Describe());
    }
}
