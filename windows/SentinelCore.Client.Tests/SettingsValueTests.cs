using System.Text.Json;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

// The settings screen is schema-driven: the server describes its own sections and this app renders
// them without knowing what any of them mean. That only works if a value of any shape survives the
// round trip untouched, and if a dotted field name reaches the right place in a nested object.
public class SettingsValueTests
{
    private static Dictionary<string, SettingsValue> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, SettingsValue>>(json, SentinelJson.Options)!;

    [Fact]
    public void Every_JSON_shape_the_settings_endpoint_mixes_decodes()
    {
        var values = Parse("""
            {"host":"127.0.0.1","port":1883,"useTls":true,"password":null,
             "topics":["a","b"],"apple":{"teamId":"ABC123","keyId":"K1"}}
            """);

        Assert.Equal(SettingsValue.ValueKind.String, values["host"].Kind);
        Assert.Equal(SettingsValue.ValueKind.Number, values["port"].Kind);
        Assert.Equal(SettingsValue.ValueKind.Bool, values["useTls"].Kind);
        Assert.Equal(SettingsValue.ValueKind.Null, values["password"].Kind);
        Assert.Equal(SettingsValue.ValueKind.Array, values["topics"].Kind);
        Assert.Equal(SettingsValue.ValueKind.Object, values["apple"].Kind);
    }

    [Fact]
    public void An_untouched_snapshot_round_trips_byte_for_byte_in_meaning()
    {
        // The save endpoint replaces a whole section, so anything this app failed to model would be
        // erased by the act of saving.
        const string json = """
            {"a":"text","b":12.5,"c":false,"d":null,"e":[1,"two",{"f":true}],"g":{"h":{"i":"deep"}}}
            """;
        var values = Parse(json);

        var written = JsonSerializer.Serialize(values, SentinelJson.Options);

        Assert.Equal(values, Parse(written));
    }

    [Fact]
    public void An_integral_number_renders_without_a_trailing_zero()
    {
        // A port has to read as 1883, not 1883.0 - it goes straight into a text box.
        Assert.Equal("1883", SettingsValue.FromNumber(1883).DisplayString);
        Assert.Equal("0.6", SettingsValue.FromNumber(0.6).DisplayString);
        Assert.Equal("-12", SettingsValue.FromNumber(-12).DisplayString);
    }

    [Fact]
    public void A_list_renders_as_the_comma_separated_form_a_csv_field_edits()
    {
        var value = SettingsValue.FromArray(new List<SettingsValue>
        {
            SettingsValue.FromString("front"),
            SettingsValue.FromString("back"),
        });

        Assert.Equal("front, back", value.DisplayString);
    }

    [Fact]
    public void A_dotted_field_name_reads_out_of_the_nested_object_it_names()
    {
        var values = Parse("""{"apple":{"teamId":"ABC123"},"flat":"here"}""");

        Assert.Equal("ABC123", SettingsValue.Read(values, "apple.teamId")?.DisplayString);
        Assert.Equal("here", SettingsValue.Read(values, "flat")?.DisplayString);
    }

    [Fact]
    public void Reading_a_path_that_is_not_there_is_null_rather_than_an_error()
    {
        var values = Parse("""{"apple":{"teamId":"ABC123"},"scalar":"x"}""");

        Assert.Null(SettingsValue.Read(values, "apple.missing"));
        Assert.Null(SettingsValue.Read(values, "missing.entirely"));
        Assert.Null(SettingsValue.Read(values, "scalar.teamId"));  // a string is not an object
        Assert.Null(SettingsValue.Read(null, "anything"));
        Assert.Null(SettingsValue.Read(values, ""));
    }

    [Fact]
    public void Writing_a_dotted_name_creates_the_nesting_and_leaves_its_siblings_alone()
    {
        var values = Parse("""{"apple":{"teamId":"ABC123","keyId":"K1"},"other":"untouched"}""");

        SettingsValue.Write(values, "apple.teamId", SettingsValue.FromString("XYZ789"));
        SettingsValue.Write(values, "firebase.projectId", SettingsValue.FromString("proj"));

        Assert.Equal("XYZ789", SettingsValue.Read(values, "apple.teamId")?.DisplayString);
        // The sibling inside the same nested object has to survive - this is the whole reason the
        // write rebuilds the chain rather than replacing the parent.
        Assert.Equal("K1", SettingsValue.Read(values, "apple.keyId")?.DisplayString);
        Assert.Equal("proj", SettingsValue.Read(values, "firebase.projectId")?.DisplayString);
        Assert.Equal("untouched", SettingsValue.Read(values, "other")?.DisplayString);
    }

    [Fact]
    public void Writing_over_a_scalar_with_a_nested_path_replaces_it_rather_than_failing()
    {
        var values = Parse("""{"apple":"was a string"}""");

        SettingsValue.Write(values, "apple.teamId", SettingsValue.FromString("ABC"));

        Assert.Equal("ABC", SettingsValue.Read(values, "apple.teamId")?.DisplayString);
    }

    [Fact]
    public void A_three_level_path_works_even_though_the_schema_only_uses_two()
    {
        var values = new Dictionary<string, SettingsValue>();

        SettingsValue.Write(values, "a.b.c", SettingsValue.FromNumber(7));

        Assert.Equal("7", SettingsValue.Read(values, "a.b.c")?.DisplayString);
    }

    [Fact]
    public void A_boolean_reads_as_false_when_the_value_is_not_a_boolean_at_all()
    {
        // Schemas and servers disagree occasionally; a checkbox has to render something either way.
        Assert.True(SettingsValue.FromBool(true).AsBool);
        Assert.False(SettingsValue.FromString("true").AsBool);
        Assert.False(SettingsValue.Null.AsBool);
    }

    [Fact]
    public void A_settings_section_schema_names_the_service_that_has_to_restart()
    {
        var schema = JsonSerializer.Deserialize<SettingsSectionSchema>("""
            {"key":"exposed","title":"INTERNET EXPOSED MODE","endpoint":"/api/settings/exposed","basic":false,
             "description":"","restartService":"dashboard","dataEndpoint":"/api/settings/exposed/data",
             "fields":[{"name":"enabled","label":"Enabled","type":"checkbox","tip":"","requiresRestart":true,"secret":false}]}
            """, SentinelJson.Options)!;

        Assert.True(schema.NeedsRestart);
        Assert.Equal("dashboard", schema.RestartTarget);
        Assert.Equal("/api/settings/exposed/data", schema.DataEndpoint);
    }

    [Fact]
    public void A_section_from_a_server_that_predates_grouping_still_decodes()
    {
        var schema = JsonSerializer.Deserialize<SettingsSectionSchema>("""
            {"key":"mqtt","title":"MQTT","endpoint":"/api/settings/mqtt","basic":true,"description":"","fields":[]}
            """, SentinelJson.Options)!;

        // Everything lands in one unnamed group, which is exactly the flat list it had before.
        Assert.Null(schema.Group);
        Assert.Null(schema.GroupOrder);
        // And with no restartService named, the bridge is the default every other section uses.
        Assert.Equal("bridge", schema.RestartTarget);
        Assert.False(schema.NeedsRestart);
    }

    [Fact]
    public void A_field_from_a_server_that_predates_the_integer_flag_applies_no_restriction()
    {
        var field = JsonSerializer.Deserialize<SettingsFieldSchema>("""
            {"name":"port","label":"Port","type":"number","tip":"","requiresRestart":false,"secret":false}
            """, SentinelJson.Options)!;

        // Null has to mean "don't apply a restriction this server never asked for" rather than
        // failing to decode the whole schema.
        Assert.Null(field.Integer);
        Assert.Null(field.Lookup);
        Assert.Null(field.OptionsSource);
        Assert.Null(field.Options);
    }
}

internal static class SentinelJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
