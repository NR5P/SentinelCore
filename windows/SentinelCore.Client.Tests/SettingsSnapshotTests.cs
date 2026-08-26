using System.Text.Json;
using SentinelCore.Client.Models;
using Xunit;

namespace SentinelCore.Client.Tests;

/// <summary>
/// Pulling one section's values out of the bulk settings snapshot.
/// </summary>
/// <remarks>
/// GET /api/settings answers keyed by section, while a schema field's name is relative to its own
/// section. Reading a field name off the top of the snapshot finds nothing, and the whole settings
/// screen renders empty against a server that is answering perfectly - no error, no clue, just
/// blank boxes over real stored values, with a Save button that would then write those blanks back.
/// </remarks>
public class SettingsSnapshotTests
{
    private static Dictionary<string, SettingsValue> Snapshot() =>
        JsonSerializer.Deserialize<Dictionary<string, SettingsValue>>("""
            {
              "mqtt": { "host": "192.168.1.10", "port": 1883, "useTls": false, "password": "••••••••" },
              "email": { "host": "smtp.example.com", "port": 587 },
              "pushNotifications": { "enabled": true, "apple": { "teamId": "ABC123", "keyId": "K1" } },
              "knownVehicles": [ { "plate": "AB12 CDE" } ]
            }
            """, SentinelJson.Options)!;

    [Fact]
    public void A_sections_own_fields_come_back_under_their_plain_names()
    {
        var values = SettingsSnapshot.SectionValues(Snapshot(), "mqtt");

        // This is the whole bug: "host" resolves inside the section, and does not exist at the top.
        Assert.Equal("192.168.1.10", SettingsValue.Read(values, "host")?.DisplayString);
        Assert.Equal("1883", SettingsValue.Read(values, "port")?.DisplayString);
        Assert.Null(SettingsValue.Read(Snapshot(), "host"));
    }

    [Fact]
    public void Sections_do_not_leak_into_one_another()
    {
        var mqtt = SettingsSnapshot.SectionValues(Snapshot(), "mqtt");
        var email = SettingsSnapshot.SectionValues(Snapshot(), "email");

        // Both have a "host" and a "port", and they are not the same values.
        Assert.Equal("192.168.1.10", SettingsValue.Read(mqtt, "host")?.DisplayString);
        Assert.Equal("smtp.example.com", SettingsValue.Read(email, "host")?.DisplayString);
        Assert.Equal("587", SettingsValue.Read(email, "port")?.DisplayString);
    }

    [Fact]
    public void A_dotted_field_name_still_resolves_inside_its_section()
    {
        var values = SettingsSnapshot.SectionValues(Snapshot(), "pushNotifications");

        Assert.True(SettingsValue.Read(values, "enabled")?.AsBool);
        Assert.Equal("ABC123", SettingsValue.Read(values, "apple.teamId")?.DisplayString);
        Assert.Equal("K1", SettingsValue.Read(values, "apple.keyId")?.DisplayString);
    }

    [Fact]
    public void A_masked_secret_arrives_as_the_mask_so_it_can_be_sent_back_unchanged()
    {
        var values = SettingsSnapshot.SectionValues(Snapshot(), "mqtt");

        Assert.Equal(SentinelSecrets.Mask, SettingsValue.Read(values, "password")?.DisplayString);
    }

    [Fact]
    public void A_section_the_snapshot_has_never_held_values_for_renders_empty_rather_than_failing()
    {
        // Not an error: a section the server knows about but has no stored values for is exactly a
        // form full of defaults.
        Assert.Empty(SettingsSnapshot.SectionValues(Snapshot(), "somethingAddedLater"));
    }

    [Fact]
    public void A_snapshot_key_that_is_not_an_object_is_not_treated_as_a_section()
    {
        // knownVehicles is an array, and asking for it as a section must not throw or invent fields.
        Assert.Empty(SettingsSnapshot.SectionValues(Snapshot(), "knownVehicles"));
    }

    [Fact]
    public void Nothing_at_all_is_survivable()
    {
        Assert.Empty(SettingsSnapshot.SectionValues(null, "mqtt"));
        Assert.Empty(SettingsSnapshot.SectionValues(Snapshot(), ""));
    }

    [Fact]
    public void The_returned_set_is_a_copy_so_editing_a_form_cannot_corrupt_the_snapshot()
    {
        var snapshot = Snapshot();
        var values = SettingsSnapshot.SectionValues(snapshot, "mqtt");

        values["host"] = SettingsValue.FromString("changed-in-the-form");

        Assert.Equal(
            "192.168.1.10",
            SettingsValue.Read(SettingsSnapshot.SectionValues(snapshot, "mqtt"), "host")?.DisplayString);
    }
}
