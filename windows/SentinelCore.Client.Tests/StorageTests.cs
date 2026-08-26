using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

public class StorageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_saved_login_comes_back_for_the_server_it_was_saved_against()
    {
        var store = new CredentialStore(_directory);

        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");

        Assert.Equal(new StoredCredential("admin", "hunter2"), store.Load("http://192.168.1.50:3320"));
    }

    [Fact]
    public void One_servers_login_is_never_offered_to_another()
    {
        // Two servers are two different logins, and reusing one against the other would send the
        // wrong password to a machine that might well log it.
        var store = new CredentialStore(_directory);
        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");

        Assert.Null(store.Load("http://192.168.1.99:3320"));
    }

    [Fact]
    public void The_same_address_written_differently_is_still_the_same_server()
    {
        var store = new CredentialStore(_directory);
        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");

        Assert.NotNull(store.Load("http://192.168.1.50:3320/"));
        Assert.NotNull(store.Load("  http://192.168.1.50:3320  "));
    }

    [Fact]
    public void A_saved_login_is_not_readable_as_plain_text_on_disk()
    {
        var store = new CredentialStore(_directory);
        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");

        var files = Directory.GetFiles(_directory);
        var contents = string.Join("", files.Select(File.ReadAllBytes).Select(Convert.ToHexString));

        Assert.DoesNotContain(Convert.ToHexString("hunter2"u8.ToArray()), contents);
    }

    [Fact]
    public void A_blob_that_no_longer_decrypts_reads_as_having_no_saved_login()
    {
        // A restored profile or a corrupted file means the same thing as never having signed in,
        // which is a normal sign-in screen - not an error path of its own.
        var store = new CredentialStore(_directory);
        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");
        foreach (var file in Directory.GetFiles(_directory)) File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });

        Assert.Null(store.Load("http://192.168.1.50:3320"));
    }

    [Fact]
    public void Signing_out_leaves_nothing_behind()
    {
        var store = new CredentialStore(_directory);
        store.Save(new StoredCredential("admin", "hunter2"), "http://192.168.1.50:3320");

        store.Delete("http://192.168.1.50:3320");

        Assert.Null(store.Load("http://192.168.1.50:3320"));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Deleting_a_login_that_was_never_saved_is_not_an_error()
    {
        var store = new CredentialStore(_directory);

        store.Delete("http://192.168.1.50:3320");
    }

    [Fact]
    public void Preferences_survive_being_reopened()
    {
        var path = Path.Combine(_directory, "settings.json");
        var first = new PreferenceStore(path);
        first.Set("serverURL", "http://192.168.1.50:3320");
        first.Set("desktopAlertsEnabled", false);
        first.Set("windowWidth", 1280.5);

        var second = new PreferenceStore(path);

        Assert.Equal("http://192.168.1.50:3320", second.GetString("serverURL"));
        Assert.False(second.GetBool("desktopAlertsEnabled", true));
        Assert.Equal(1280.5, second.GetDouble("windowWidth"));
    }

    [Fact]
    public void A_preference_that_was_never_set_falls_back()
    {
        var store = new PreferenceStore(Path.Combine(_directory, "settings.json"));

        Assert.Equal("", store.GetString("serverURL"));
        Assert.True(store.GetBool("desktopAlertsEnabled", true));
        Assert.Equal(42, store.GetDouble("nothing", 42));
    }

    [Fact]
    public void A_corrupted_preferences_file_reads_as_a_fresh_install_rather_than_crashing()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        var store = new PreferenceStore(path);

        Assert.Equal("", store.GetString("serverURL"));
        // And it still writes from there, rather than being wedged.
        store.Set("serverURL", "http://192.168.1.50:3320");
        Assert.Equal("http://192.168.1.50:3320", new PreferenceStore(path).GetString("serverURL"));
    }

    [Fact]
    public void A_preference_of_the_wrong_type_falls_back_instead_of_throwing()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{"desktopAlertsEnabled":"yes please","windowWidth":"wide"}""");

        var store = new PreferenceStore(path);

        Assert.True(store.GetBool("desktopAlertsEnabled", true));
        Assert.Equal(1100, store.GetDouble("windowWidth", 1100));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ServerVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("1.2.0-beta.1", 1, 2, 0)]        // pre-release metadata is dropped, not parsed
    [InlineData("1.0.0+ab12cd.dirty", 1, 0, 0)]  // nothing should refuse to compare a dirty build
    [InlineData("  1.4.2  ", 1, 4, 2)]
    public void A_version_string_parses_down_to_the_numbers(string text, int major, int minor, int patch) =>
        Assert.Equal(new ServerVersion(major, minor, patch), ServerVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a version")]
    public void Something_that_is_not_a_version_parses_to_nothing(string? text) =>
        Assert.Null(ServerVersion.Parse(text));

    [Fact]
    public void Versions_order_by_major_then_minor_then_patch()
    {
        Assert.True(new ServerVersion(1, 0, 0) < new ServerVersion(1, 0, 1));
        Assert.True(new ServerVersion(1, 9, 9) < new ServerVersion(2, 0, 0));
        Assert.True(new ServerVersion(1, 2, 0) >= new ServerVersion(1, 2, 0));
    }

    [Fact]
    public void A_server_that_reports_no_build_at_all_predates_version_reporting()
    {
        var compatibility = ServerCompatibility.Evaluate(null);

        Assert.Equal(ServerCompatibilityKind.PredatesVersionReporting, compatibility.Kind);
        Assert.Contains("too old to report its version", compatibility.Advisory);
    }

    [Fact]
    public void A_current_server_has_nothing_worth_saying()
    {
        var compatibility = ServerCompatibility.Evaluate(new ServerBuild { Version = "1.0.0" });

        Assert.Equal(ServerCompatibilityKind.Ok, compatibility.Kind);
        Assert.Null(compatibility.Advisory);
    }

    [Fact]
    public void A_build_whose_version_cannot_be_read_says_nothing_rather_than_guessing()
    {
        var compatibility = ServerCompatibility.Evaluate(new ServerBuild { Version = "unknown" });

        Assert.Equal(ServerCompatibilityKind.Unknown, compatibility.Kind);
        Assert.Null(compatibility.Advisory);
    }

    [Fact]
    public void A_server_older_than_the_minimum_gets_an_advisory_naming_both_versions()
    {
        var compatibility = ServerCompatibility.Evaluate(new ServerBuild { Version = "0.9.0" });

        Assert.Equal(ServerCompatibilityKind.Outdated, compatibility.Kind);
        Assert.Contains("0.9.0", compatibility.Advisory);
        Assert.Contains(ServerVersion.MinimumSupported.ToString(), compatibility.Advisory);
        // Advisory, never blocking: the wording has to say most things still work.
        Assert.Contains("Most things still work", compatibility.Advisory);
    }
}

public class MeasurementFormattingTests
{
    [Theory]
    [InlineData(21.500000000000004, "21.5")]  // float noise picked up from scaling upstream
    [InlineData(21.0, "21")]
    [InlineData(0.0, "0")]
    [InlineData(-0.001, "0")]                 // rounds to -0, which reads wrong
    [InlineData(1013.25, "1013.25")]
    [InlineData(-4.5, "-4.5")]
    public void A_reading_renders_the_way_the_dashboard_renders_it(double value, string expected) =>
        Assert.Equal(expected, new HubMeasurement { Key = "temperature", Value = value }.FormattedValue);

    [Fact]
    public void A_reading_that_is_not_a_number_renders_as_a_dash()
    {
        Assert.Equal("-", new HubMeasurement { Value = double.NaN }.FormattedValue);
        Assert.Equal("-", new HubMeasurement { Value = double.PositiveInfinity }.FormattedValue);
    }

    [Fact]
    public void A_device_publishing_numbers_but_no_on_off_is_reporting_not_unknown()
    {
        var sensor = new HubDevice
        {
            Kind = "sensor",
            Commandable = false,
            Power = "unknown",
            Measurements = new List<HubMeasurement> { new() { Key = "temperature", Value = 21.5 } },
        };

        Assert.True(sensor.IsReporting);
    }

    [Fact]
    public void A_device_with_nothing_to_say_stays_unknown()
    {
        var sensor = new HubDevice { Kind = "sensor", Commandable = false, Power = "unknown" };

        Assert.False(sensor.IsReporting);
    }

    [Fact]
    public void Kinds_that_speak_open_and_close_are_told_apart_from_ones_that_speak_on_and_off()
    {
        Assert.True(new HubDevice { Kind = "gate" }.IsCoverLike);
        Assert.True(new HubDevice { Kind = "garage" }.IsCoverLike);
        Assert.True(new HubDevice { Kind = "cover" }.IsCoverLike);
        Assert.False(new HubDevice { Kind = "light" }.IsCoverLike);
        Assert.True(new HubDevice { Kind = "lock" }.IsLock);
    }

    [Fact]
    public void A_button_is_momentary_even_on_a_server_that_does_not_say_so()
    {
        // The server folds the "button" kind into the flag, but the kind check keeps older servers
        // honest too.
        Assert.True(new HubDevice { Kind = "button" }.IsMomentary);
        Assert.True(new HubDevice { Kind = "switch", Momentary = true }.IsMomentary);
        Assert.False(new HubDevice { Kind = "switch" }.IsMomentary);
    }

    [Fact]
    public void A_device_with_no_stop_payload_has_no_stop_control()
    {
        Assert.False(new HubDevice { Kind = "gate" }.HasStop);
        Assert.True(new HubDevice { Kind = "gate", PayloadStop = "STOP" }.HasStop);
    }
}
