using System.Net;
using System.Text.Json;
using SentinelCore.Client;
using SentinelCore.Client.Models;
using SentinelCore.Client.Networking;
using Xunit;

namespace SentinelCore.Client.Tests;

public class SentinelClientTests
{
    private static SentinelClient ClientFor(FakeServer server) =>
        new(() => server.BaseUrl, NullSentinelLog.Instance);

    [Fact]
    public async Task Activity_asks_for_the_whole_day_and_hides_rejected_motion_by_default()
    {
        using var server = new FakeServer().OnGet("/api/activity", "[]");
        using var client = ClientFor(server);

        await client.ActivityAsync();

        // The 500 limit is load-bearing: this list is the day-level audit trail now that it carries
        // motion as well as analyses, so a busy camera's whole day has to fit.
        Assert.Equal("GET /api/activity?limit=500", server.Requests.Single());
    }

    [Fact]
    public async Task Activity_passes_the_show_hidden_and_notified_filters_through_to_the_server()
    {
        using var server = new FakeServer().OnGet("/api/activity", "[]");
        using var client = ClientFor(server);

        await client.ActivityAsync(search: "front door", notifiedOnly: true, includeUnconfirmed: true);

        var request = server.Requests.Single();
        Assert.Contains("search=front%20door", request);
        Assert.Contains("notifiedOnly=true", request);
        Assert.Contains("includeUnconfirmed=true", request);
    }

    [Fact]
    public async Task An_activity_row_decodes_both_kinds_the_server_merges()
    {
        const string json = """
            [
              {
                "id": "8f14e45f-ceea-467a-9575-9f9a4b3d1f2c",
                "kind": "analyzed",
                "camera": "Front Door",
                "occurredAt": "2026-08-25T09:14:03.1234567+01:00",
                "imageUrl": "/api/events/8f14e45f-ceea-467a-9575-9f9a4b3d1f2c/image",
                "status": "ok",
                "eventType": "package_delivery",
                "summary": "A courier left a parcel.",
                "notified": true,
                "confidence": 0.92,
                "clipState": "ready",
                "clipUrl": "/api/recordings/1/video",
                "aiAnalysisOff": false
              },
              {
                "id": "trigger:1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed",
                "kind": "motion",
                "camera": "Garden",
                "occurredAt": "2026-08-25T09:10:00Z",
                "status": "ok",
                "notified": false,
                "zoneName": "Back gate",
                "confirmation": "rejected"
              }
            ]
            """;
        using var server = new FakeServer().OnGet("/api/activity", json);
        using var client = ClientFor(server);

        var items = await client.ActivityAsync();

        var analyzed = items[0];
        Assert.True(analyzed.IsAnalyzed);
        Assert.True(analyzed.HasClip);
        Assert.Equal(ActivityClipState.Ready, analyzed.Clip);
        Assert.Equal(Guid.Parse("8f14e45f-ceea-467a-9575-9f9a4b3d1f2c"), analyzed.AnalysisId);
        Assert.Equal("A courier left a parcel.", analyzed.AsSecurityEvent()?.Summary);

        // A motion row's id is prefixed so the two sources can't collide in one list, and it has no
        // alert behind it - which is exactly why AnalysisId has to come back null rather than throw.
        var motion = items[1];
        Assert.False(motion.IsAnalyzed);
        Assert.Null(motion.AnalysisId);
        Assert.Null(motion.AsSecurityEvent());
        Assert.Null(motion.Clip);
        Assert.Equal("Back gate", motion.ZoneName);
    }

    [Fact]
    public async Task An_older_server_that_sends_none_of_the_newer_fields_still_decodes()
    {
        // A decode that fails here is a blank Events tab, not a missing badge - so the absence of
        // every optional field has to be survivable.
        const string json = """
            [{"id":"8f14e45f-ceea-467a-9575-9f9a4b3d1f2c","kind":"analyzed","camera":"Front Door",
              "occurredAt":"2026-08-25T09:14:03Z","status":"ok","notified":false}]
            """;
        using var server = new FakeServer().OnGet("/api/activity", json);
        using var client = ClientFor(server);

        var item = Assert.Single(await client.ActivityAsync());

        Assert.False(item.IsAiAnalysisOff);   // absent reads as "analysis is on"
        Assert.Null(item.Clip);
        Assert.False(item.HasClip);
    }

    [Fact]
    public async Task A_401_becomes_the_sign_in_signal_rather_than_a_generic_failure()
    {
        using var server = new FakeServer().OnGet("/api/cameras", "{}", HttpStatusCode.Unauthorized);
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<SentinelAuthenticationRequiredException>(() => client.CamerasAsync());
    }

    [Fact]
    public async Task The_servers_own_error_message_survives_all_three_shapes_it_uses()
    {
        // {error}, {errors:[]} and RFC 7807's {detail} all have to be read - only reading the first
        // two turns every Results.Problem into a bare "Server request failed (HTTP 502)" with the
        // sentence explaining what to do about it thrown away.
        using var server = new FakeServer()
            .OnGet("/api/cameras", """{"error":"That camera name is taken."}""", HttpStatusCode.BadRequest)
            .OnGet("/api/accounts", """{"errors":["Too short.","No spaces."]}""", HttpStatusCode.BadRequest)
            .OnGet("/api/devices", """{"detail":"Could not grab a frame - check the stream URL."}""", HttpStatusCode.BadGateway);
        using var client = ClientFor(server);

        var single = await Assert.ThrowsAsync<SentinelServerException>(() => client.CamerasAsync());
        Assert.Equal("That camera name is taken.", single.Message);

        var many = await Assert.ThrowsAsync<SentinelServerException>(() => client.AccountsAsync());
        Assert.Equal("Too short.\nNo spaces.", many.Message);

        var problem = await Assert.ThrowsAsync<SentinelServerException>(() => client.DevicesAsync());
        Assert.Equal("Could not grab a frame - check the stream URL.", problem.Message);
        Assert.Equal(502, problem.Status);
    }

    [Fact]
    public async Task An_error_with_no_readable_body_still_names_the_status()
    {
        using var server = new FakeServer().OnGet("/api/cameras", "not json at all", HttpStatusCode.InternalServerError);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.CamerasAsync());

        Assert.Equal("Server request failed (HTTP 500).", error.Message);
    }

    [Fact]
    public async Task The_session_cookie_is_carried_from_login_onto_later_requests()
    {
        using var server = new FakeServer()
            .OnSetsCookie("POST", "/api/auth/login", "SentinelAuth=abc123; Path=/")
            .OnGet("/api/auth/status", """{"enabled":true,"configured":true,"authenticated":true,"role":"Admin"}""");
        using var client = ClientFor(server);

        await client.LoginAsync("admin", "hunter2");
        var status = await client.AuthStatusAsync();

        // Everything after the login has to ride the session the login established - the whole
        // reason this client keeps a cookie jar rather than a bearer token.
        Assert.True(status.Authenticated);
        Assert.Equal("", server.CookieHeaders[0]);                      // the login itself carried none
        Assert.Contains("SentinelAuth=abc123", server.CookieHeaders[1]); // the status call did
    }

    [Fact]
    public async Task Resetting_the_session_throws_the_cookies_away()
    {
        using var server = new FakeServer()
            .OnSetsCookie("POST", "/api/auth/login", "SentinelAuth=abc123; Path=/")
            .OnEchoesCookie("/api/auth/status");
        using var client = ClientFor(server);

        await client.LoginAsync("admin", "hunter2");
        Assert.Contains("SentinelAuth=abc123", CookieHeaderFor(client, server));

        client.ResetSession();

        // A fresh session is the whole point of the reset: whatever the old one was holding is gone.
        Assert.DoesNotContain("SentinelAuth", CookieHeaderFor(client, server));
    }

    private static string CookieHeaderFor(SentinelClient client, FakeServer server) =>
        string.Join(
            "; ",
            client.Cookies.GetCookies(new Uri(server.BaseUrl)).Select(cookie => $"{cookie.Name}={cookie.Value}"));

    [Fact]
    public async Task A_trigger_config_that_predates_multi_zone_still_yields_one_zone()
    {
        const string json = """
            {"enabled":true,"zonePolygon":[{"x":0.1,"y":0.1},{"x":0.9,"y":0.1},{"x":0.9,"y":0.9}],
             "minTriggerArea":0.02,"cooldownSeconds":30,"maxTriggersPerMinute":6,"shadowSensitivity":50,
             "recordClips":true,"clipPreSeconds":5,"clipPostSeconds":10,"sendToAlerts":true,"alertProfile":"default"}
            """;
        using var server = new FakeServer().OnGet($"/api/cameras/{Guid.Empty}/trigger-config", json);
        using var client = ClientFor(server);

        var config = await client.TriggerConfigAsync(Guid.Empty);

        // A config written before multi-zone reads as a single zone built from the legacy pair, so
        // the editor never has to special-case the migration.
        var zone = Assert.Single(config.ResolvedZones);
        Assert.Equal(TriggerZone.DefaultName, zone.Name);
        Assert.Equal(3, zone.Polygon.Count);
        Assert.Equal(0.02, zone.MinTriggerArea);

        // A server older than the night window sends none of the three keys; the editor still needs
        // values, and they have to match the server's own defaults rather than a zero-width window.
        Assert.False(config.ResolvedNightEnabled);
        Assert.Equal(21, config.ResolvedNightStartHour);
        Assert.Equal(7, config.ResolvedNightEndHour);

        // Same story for the two AI routing flags, whose columns are newer still: absent means the
        // server predates them, and both of the server's own defaults are off.
        Assert.False(config.ResolvedBypassFastLocal);
        Assert.False(config.ResolvedKeepOnlyAlertable);
        Assert.Null(config.BypassFastLocal);
        Assert.Null(config.KeepOnlyAlertable);
    }

    [Fact]
    public async Task The_AI_routing_flags_are_read_back_as_the_server_sent_them()
    {
        const string json = """
            {"enabled":true,"zonePolygon":[],"minTriggerArea":0.02,"cooldownSeconds":30,
             "maxTriggersPerMinute":6,"shadowSensitivity":50,"recordClips":false,"clipPreSeconds":5,
             "clipPostSeconds":10,"sendToAlerts":true,"alertProfile":"coop",
             "bypassFastLocal":true,"keepOnlyAlertable":true}
            """;
        using var server = new FakeServer().OnGet($"/api/cameras/{Guid.Empty}/trigger-config", json);
        using var client = ClientFor(server);

        var config = await client.TriggerConfigAsync(Guid.Empty);

        Assert.True(config.ResolvedBypassFastLocal);
        Assert.True(config.ResolvedKeepOnlyAlertable);
    }

    [Fact]
    public async Task Saving_a_trigger_config_never_sends_a_null_night_window_as_a_plain_false()
    {
        // The trap this guards: null omits the key, which the server reads as "this client doesn't
        // know about the night window" and merges against the stored row. A build that sent a plain
        // false on every save would switch someone's window off whenever they nudged a polygon.
        using var server = new FakeServer().On("PUT", $"/api/cameras/{Guid.Empty}/trigger-config", "{}");
        using var client = ClientFor(server);

        await client.SaveTriggerConfigAsync(Guid.Empty, new TriggerConfig { Enabled = true });

        var body = server.Bodies.Single();
        Assert.DoesNotContain("nightWindowEnabled", body);
        Assert.DoesNotContain("zones", body);      // absent, not [], which would mean "delete them"
        Assert.Contains("zonePolygon", body);      // must keep being sent whatever else changes
        Assert.Contains("minTriggerArea", body);

        // The two AI routing flags are the same trap with a newer name: a plain false here would
        // route the coop camera back through the fast detector and re-widen what it keeps, both
        // without anyone asking. Null has to stay off the wire entirely.
        Assert.DoesNotContain("bypassFastLocal", body);
        Assert.DoesNotContain("keepOnlyAlertable", body);
    }

    [Fact]
    public async Task Saving_a_trigger_config_sends_the_AI_routing_flags_the_editor_set()
    {
        // The other half of the contract: a screen that knows about the flags owns their value, so
        // an explicit false has to reach the server rather than being mistaken for "no opinion".
        using var server = new FakeServer().On("PUT", $"/api/cameras/{Guid.Empty}/trigger-config", "{}");
        using var client = ClientFor(server);

        await client.SaveTriggerConfigAsync(
            Guid.Empty,
            new TriggerConfig { Enabled = true, BypassFastLocal = true, KeepOnlyAlertable = false });

        var body = server.Bodies.Single();
        Assert.Contains("\"bypassFastLocal\":true", body);
        Assert.Contains("\"keepOnlyAlertable\":false", body);
    }

    [Fact]
    public async Task Saving_a_trigger_config_sends_an_empty_zone_list_when_the_editor_really_cleared_it()
    {
        using var server = new FakeServer().On("PUT", $"/api/cameras/{Guid.Empty}/trigger-config", "{}");
        using var client = ClientFor(server);

        await client.SaveTriggerConfigAsync(Guid.Empty, new TriggerConfig { Zones = new List<TriggerZone>() });

        Assert.Contains("\"zones\":[]", server.Bodies.Single());
    }

    [Fact]
    public async Task A_licence_install_is_sent_as_the_files_own_text_not_wrapped_in_JSON()
    {
        using var server = new FakeServer()
            .On("POST", "/api/license/install", """{"valid":true,"customerName":"Acme Ltd"}""");
        using var client = ClientFor(server);

        // The endpoint takes the file's bytes, not a path, because the server has no way to reach
        // whatever machine the file was emailed to - and it expects them as text/plain.
        var state = await client.InstallLicenseAsync("-----BEGIN LICENSE-----\nabc\n-----END LICENSE-----");

        Assert.True(state.Valid);
        Assert.Equal("Acme Ltd", state.CustomerName);
        Assert.StartsWith("text/plain", server.ContentTypes.Single());
        Assert.Contains("-----BEGIN LICENSE-----", server.Bodies.Single());
    }

    [Fact]
    public async Task A_backup_travels_as_raw_bytes_in_both_directions()
    {
        // A client that decoded a backup into a fixed shape would quietly drop any section added
        // after this app was built - the one thing a backup must never do.
        var payload = """{"cameras":[{"name":"Front Door"}],"somethingAddedLater":{"keepMe":true}}"""u8.ToArray();
        using var server = new FakeServer()
            .OnBytes("/api/backup/export", payload, "application/json")
            .On("POST", "/api/backup/import", """{"camerasRestored":1}""");
        using var client = ClientFor(server);

        var exported = await client.ExportBackupAsync();
        Assert.Equal(payload, exported);

        var result = await client.ImportBackupAsync(exported);
        Assert.Equal(1, result.CamerasRestored);
        Assert.Equal("1 camera", result.Summary);
        Assert.Contains("somethingAddedLater", server.Bodies.Last());
    }

    [Fact]
    public async Task A_restore_summary_names_only_the_sections_that_had_anything_in_them()
    {
        using var server = new FakeServer()
            .On("POST", "/api/backup/import", """{"camerasRestored":3,"triggerZonesRestored":1,"accountsRestored":0}""");
        using var client = ClientFor(server);

        var result = await client.ImportBackupAsync("{}"u8.ToArray());

        // "0 accounts" would read like something went missing rather than like nothing was there.
        Assert.Equal("3 cameras, 1 trigger zone", result.Summary);
    }

    [Fact]
    public async Task A_restore_that_put_nothing_countable_back_still_says_something()
    {
        using var server = new FakeServer().On("POST", "/api/backup/import", "{}");
        using var client = ClientFor(server);

        Assert.Equal("settings", (await client.ImportBackupAsync("{}"u8.ToArray())).Summary);
    }

    [Fact]
    public async Task Known_vehicles_are_read_out_of_the_settings_snapshot()
    {
        const string json = """
            {"knownVehicles":[{"plate":"AB12 CDE","label":"Mum's car"},{"plate":"XY99 ZZZ","label":"Neighbour"}]}
            """;
        using var server = new FakeServer().OnGet("/api/settings", json);
        using var client = ClientFor(server);

        var vehicles = await client.KnownVehiclesAsync();

        Assert.Equal(2, vehicles.Count);
        Assert.Equal("AB12 CDE", vehicles[0].Plate);
        Assert.Equal("Mum's car", vehicles[0].Label);
    }

    [Fact]
    public async Task A_settings_snapshot_with_no_vehicles_section_reads_as_an_empty_list()
    {
        using var server = new FakeServer().OnGet("/api/settings", """{"mqtt":{"host":"127.0.0.1"}}""");
        using var client = ClientFor(server);

        Assert.Empty(await client.KnownVehiclesAsync());
    }

    [Fact]
    public async Task HomeKit_settings_fall_back_to_the_servers_own_defaults_when_the_section_is_absent()
    {
        using var server = new FakeServer().OnGet("/api/settings", "{}");
        using var client = ClientFor(server);

        var settings = await client.HomeKitSettingsAsync();

        Assert.False(settings.Enabled);
        Assert.Equal("http://127.0.0.1:3321", settings.SidecarBaseUrl);
        Assert.Equal(10, settings.TimeoutSeconds);
        Assert.Equal(0.6, settings.MinimumConfidence);
        Assert.False(settings.IncludeDevices);
        Assert.False(settings.IncludeVideo);
    }

    // The save merges the whole DTO on the server, so a switch the client never read back would be
    // written as false - turning off the Device Hub or live video by saving an unrelated field.
    [Fact]
    public async Task HomeKit_settings_read_both_include_switches()
    {
        using var server = new FakeServer().OnGet("/api/settings",
            "{\"homeKit\":{\"enabled\":true,\"includeDevices\":true,\"includeVideo\":true}}");
        using var client = ClientFor(server);

        var settings = await client.HomeKitSettingsAsync();

        Assert.True(settings.IncludeDevices);
        Assert.True(settings.IncludeVideo);
    }

    [Fact]
    public async Task Resetting_HomeKit_sends_the_confirmation_string_the_endpoint_requires()
    {
        using var server = new FakeServer().On("POST", "/api/homekit/reset", "{}");
        using var client = ClientFor(server);

        await client.ResetHomeKitPairingAsync();

        // An explicit confirmation string rather than a bare POST, since this drops the pairing and
        // every Home app on the network has to pair again.
        Assert.Contains("\"confirm\":\"RESET\"", server.Bodies.Single());
    }

    [Fact]
    public async Task Prompt_profiles_round_trip_the_fields_this_app_does_not_model()
    {
        // The save endpoint replaces the whole PromptProfiles dictionary, so anything this app
        // failed to model would be erased by the act of saving.
        const string json = """
            {"promptProfiles":{"default":{"displayName":"Default","prompt":"Describe.",
             "cropRegion":{"x":0.1,"y":0.2,"width":0.5,"height":0.5}}}}
            """;
        using var server = new FakeServer()
            .OnGet("/api/settings", json)
            .On("PUT", "/api/settings/prompt-profiles", "{}");
        using var client = ClientFor(server);

        var profiles = await client.PromptProfilesAsync();
        await client.SavePromptProfilesAsync(profiles);

        var saved = server.Bodies.Last();
        Assert.Contains("cropRegion", saved);
        Assert.Contains("\"width\":0.5", saved);
    }

    [Fact]
    public void Url_keeps_a_query_string_the_server_put_on_a_relative_path()
    {
        using var server = new FakeServer();
        using var client = ClientFor(server);

        // The activity feed's imageUrl carries a cache-busting token; splitting it off loses it.
        var url = client.Url("/api/events/abc/image?v=12345");

        Assert.Equal($"{server.BaseUrl}/api/events/abc/image?v=12345", url?.ToString());
    }

    [Fact]
    public async Task Url_is_null_rather_than_wrong_when_no_server_is_configured()
    {
        using var client = new SentinelClient(() => "", NullSentinelLog.Instance);

        Assert.Null(client.Url("/api/cameras"));
        await Assert.ThrowsAsync<SentinelInvalidAddressException>(() => client.CamerasAsync());
    }

    [Fact]
    public async Task ProbeReachable_counts_any_answer_including_a_refusal()
    {
        // The point is to detect whether the host is reachable at all, not to validate the
        // response - a 401 from a login-protected server still proves it is there.
        using var server = new FakeServer().OnGet("/api/auth/status", "{}", HttpStatusCode.Unauthorized);
        using var client = ClientFor(server);

        Assert.True(await client.ProbeReachableAsync(server.BaseUrl));
    }

    [Fact]
    public async Task ProbeReachable_gives_up_promptly_on_an_address_with_nothing_behind_it()
    {
        using var client = new SentinelClient(() => "", NullSentinelLog.Instance);

        // 127.0.0.2 on a port nothing listens on: a connection refusal, not a hang.
        var reachable = await client.ProbeReachableAsync("http://127.0.0.2:9", TimeSpan.FromSeconds(3));

        Assert.False(reachable);
    }

    [Fact]
    public async Task Deleting_a_camera_sends_DELETE_with_no_body_to_expect_back()
    {
        using var server = new FakeServer().On("DELETE", $"/api/cameras/{Guid.Empty}", "", HttpStatusCode.NoContent);
        using var client = ClientFor(server);

        await client.DeleteCameraAsync(Guid.Empty);

        Assert.Equal($"DELETE /api/cameras/{Guid.Empty}", server.Requests.Single());
    }

    [Fact]
    public async Task A_camera_password_is_fetched_on_demand_and_never_arrives_with_the_list()
    {
        using var server = new FakeServer()
            .OnGet("/api/cameras", $$"""[{"id":"{{Guid.Empty}}","name":"Front Door","password":"••••••••"}]""")
            .OnGet($"/api/cameras/{Guid.Empty}/password", """{"password":"hunter2"}""");
        using var client = ClientFor(server);

        var camera = Assert.Single(await client.CamerasAsync());
        Assert.Equal(SentinelSecrets.Mask, camera.Password);

        Assert.Equal("hunter2", await client.CameraPasswordAsync(Guid.Empty));
    }

    [Fact]
    public async Task Schedule_metadata_decodes_the_darkness_levels_the_server_names()
    {
        const string json = """
            {"kinds":["daily","solar"],"solarEvents":["sunset","dusk"],
             "darknessLevels":[{"key":"civil","label":"Civil twilight","description":"Streetlights on","elevationDegrees":-6}],
             "actionTypes":["device"],"catchUpPolicies":["skip"],"deviceActions":["on","off"],"tokens":[],
             "minimumIntervalSeconds":10,"locationSet":true,"timeZone":"Europe/London","devices":[]}
            """;
        using var server = new FakeServer().OnGet("/api/schedules/metadata", json);
        using var client = ClientFor(server);

        var metadata = await client.ScheduleMetadataAsync();

        var level = Assert.Single(metadata.DarknessLevels);
        Assert.Equal("civil", level.Key);
        Assert.Equal(-6, level.ElevationDegrees);
        Assert.True(metadata.LocationSet);
    }

    [Fact]
    public async Task A_settings_section_save_sends_the_masked_secret_back_unchanged()
    {
        using var server = new FakeServer().On("PUT", "/api/settings/email", """{"saved":true}""");
        using var client = ClientFor(server);

        var values = new Dictionary<string, SettingsValue>
        {
            ["host"] = SettingsValue.FromString("smtp.example.com"),
            ["password"] = SettingsValue.FromString(SentinelSecrets.Mask),
            ["port"] = SettingsValue.FromNumber(587),
            ["useTls"] = SettingsValue.FromBool(true),
        };

        var result = await client.SaveSettingsSectionAsync("/api/settings/email", values);

        Assert.True(result.Saved);
        var body = server.Bodies.Single();

        // Sending the mask back unchanged is what tells the server to keep the one it has. It
        // travels as • escapes, because the default JSON encoder escapes non-ASCII - and it
        // stays that way deliberately: the same encoder escapes < > and &, and these values are
        // rendered into the web dashboard's HTML. What matters is that it decodes back to the mask.
        var decoded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
        Assert.Equal(SentinelSecrets.Mask, decoded["password"].GetString());
        Assert.Equal("smtp.example.com", decoded["host"].GetString());
        Assert.Equal(587, decoded["port"].GetInt32());
        Assert.True(decoded["useTls"].GetBoolean());
    }

    [Fact]
    public async Task A_settings_action_that_answers_with_an_unexpected_shape_still_counts_as_success()
    {
        // An action that succeeded but couldn't be read is still an action that succeeded; a failure
        // has already been thrown by then with the server's own message.
        using var server = new FakeServer().On("POST", "/api/setup/reset", """{"somethingElse":true}""");
        using var client = ClientFor(server);

        var detail = await client.RunSettingsActionAsync(new SettingsActionSchema
        {
            Endpoint = "/api/setup/reset",
            Method = "POST",
        });

        Assert.Null(detail);
    }

    [Fact]
    public async Task A_settings_action_hands_back_the_detail_when_there_is_one()
    {
        using var server = new FakeServer().On("POST", "/api/notifications/test/email", """{"detail":"Test email sent to a@b.c"}""");
        using var client = ClientFor(server);

        var detail = await client.RunSettingsActionAsync(new SettingsActionSchema
        {
            Endpoint = "/api/notifications/test/email",
            Method = "POST",
        });

        Assert.Equal("Test email sent to a@b.c", detail);
    }

    [Fact]
    public async Task A_response_that_is_not_the_shape_this_app_expects_is_reported_as_such()
    {
        using var server = new FakeServer().OnGet("/api/cameras", """{"notAnArray":true}""");
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<SentinelInvalidResponseException>(() => client.CamerasAsync());
    }

    [Fact]
    public async Task Dates_decode_whether_or_not_the_server_sends_fractional_seconds()
    {
        // ASP.NET Core emits DateTimeOffset values with fractional seconds on some paths and
        // without on others; both shapes have to land.
        const string json = """
            [{"id":"8f14e45f-ceea-467a-9575-9f9a4b3d1f2c","kind":"analyzed","camera":"A",
              "occurredAt":"2026-08-25T09:14:03.1234567+01:00","status":"ok","notified":false},
             {"id":"1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed","kind":"analyzed","camera":"B",
              "occurredAt":"2026-08-25T08:14:03Z","status":"ok","notified":false}]
            """;
        using var server = new FakeServer().OnGet("/api/activity", json);
        using var client = ClientFor(server);

        var items = await client.ActivityAsync();

        Assert.Equal(DateTimeOffset.Parse("2026-08-25T09:14:03.1234567+01:00"), items[0].OccurredAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-25T08:14:03Z"), items[1].OccurredAt);
    }

    [Fact]
    public async Task Live_order_is_sent_as_the_whole_arrangement_first_camera_first()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var server = new FakeServer().On("PUT", "/api/live-order", """{"cameraIds":[]}""");
        using var client = ClientFor(server);

        await client.SaveLiveOrderAsync(new List<Guid> { first, second });

        var body = server.Bodies.Single();
        Assert.Contains(first.ToString(), body);
        Assert.True(body.IndexOf(first.ToString(), StringComparison.OrdinalIgnoreCase) <
                    body.IndexOf(second.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_device_command_omits_the_field_it_is_not_setting()
    {
        using var server = new FakeServer().On("POST", $"/api/devices/{Guid.Empty}/command", """{"sent":true,"acknowledged":true,"power":"on"}""");
        using var client = ClientFor(server);

        await client.SendDeviceCommandAsync(Guid.Empty, action: "on");

        // A brightness of 0 and "don't touch the brightness" are different instructions.
        var body = server.Bodies.Single();
        Assert.Contains("\"action\":\"on\"", body);
        Assert.DoesNotContain("brightness", body);
    }

    [Fact]
    public async Task Numeric_sensor_readings_survive_a_server_that_does_not_send_them()
    {
        const string json = """
            [{"id":"8f14e45f-ceea-467a-9575-9f9a4b3d1f2c","name":"Porch light","kind":"light","room":"Outside",
              "enabled":true,"commandTopic":"a","stateTopic":"b","availabilityTopic":"","payloadOn":"ON",
              "payloadOff":"OFF","payloadAvailable":"online","payloadNotAvailable":"offline","schema":"raw",
              "stateValueTemplate":"","optimistic":false,"supportsBrightness":false,"brightnessCommandTopic":"",
              "brightnessStateTopic":"","brightnessScale":255,"discoverySource":"manual","power":"on",
              "available":true,"commandable":true}]
            """;
        using var server = new FakeServer().OnGet("/api/devices", json);
        using var client = ClientFor(server);

        var device = Assert.Single(await client.DevicesAsync());

        // A synthesized decode that threw on the missing key would stop the whole Device Hub
        // rendering rather than just this one field.
        Assert.Null(device.Measurements);
        Assert.True(device.IsOn);
        Assert.False(device.IsReporting);
    }

    // MARK: - Family Files

    [Fact]
    public async Task A_Family_Files_status_decodes_drives_the_nightly_pass_the_trash_and_a_running_job()
    {
        const string json = """
            {"enabled":true,"isAdmin":true,"username":"morgan","libraryFolder":"/srv/family","maxUploadBytes":2147483648,
             "library":{"files":1234,"bytes":9876543210,"indexedUtc":"2026-09-25T02:00:00Z"},
             "drives":[{"id":"d1","name":"USB drive","kind":"folder","enabled":true,"location":"/mnt/usb/Family Files",
                        "folderPath":"/mnt/usb/Family Files","sftpHost":"","sftpPort":22,"sftpUsername":"","sftpFolder":"",
                        "sftpHostKeyFingerprint":"","hasPassword":false,"lastAttemptUtc":"2026-09-25T02:05:00Z",
                        "lastSuccessUtc":"2026-09-25T02:05:00Z","lastResult":"ok","lastMessage":"Copied 3 files.",
                        "lastFileCount":1234,"lastBytes":9876543210,"lastCopied":3,"lastRemoved":0,
                        "lastFreeBytes":50000000000,"lastHostKeyFingerprint":null}],
             "nightly":{"hour":2,"minute":0,"removeDuplicates":true,"trashKeepDays":30,"nextRunUtc":"2026-09-26T02:00:00Z",
                        "lastRunUtc":"2026-09-25T02:00:00Z","lastResult":"ok","lastMessage":"Backed up to 1 drive."},
             "duplicates":{"ranUtc":"2026-09-25T02:01:00Z","removed":true,"groups":2,"copies":3,"bytes":4096,
                           "filesChecked":1234,"trashBatchId":"b9"},
             "trash":{"batches":1,"files":3,"bytes":4096},
             "job":{"kind":"sync","driveId":"d1","driveName":"USB drive","startedUtc":"2026-09-25T10:00:00Z",
                    "message":"Copying beach.jpg","done":40,"total":100}}
            """;
        using var server = new FakeServer().OnGet("/api/family-files/status", json);
        using var client = ClientFor(server);

        var status = await client.FamilyFilesStatusAsync();

        Assert.True(status.Enabled);
        Assert.True(status.IsAdmin);
        Assert.Equal(2147483648L, status.MaxUploadBytes);
        Assert.Equal(1234, status.Library?.Files);
        var drive = Assert.Single(status.Drives);
        Assert.Equal("USB drive", drive.Name);
        Assert.Equal("ok", drive.LastResult);
        Assert.Equal(50000000000L, drive.LastFreeBytes);
        Assert.False(drive.IsSftp);
        Assert.Equal(2, status.Nightly?.Hour);
        Assert.True(status.Nightly?.RemoveDuplicates);
        Assert.Equal(3, status.Duplicates?.Copies);
        Assert.Equal(3, status.Trash?.Files);
        Assert.Equal("sync", status.Job?.Kind);
        Assert.Equal(40, status.Job?.Done);
        Assert.Equal("Backing up to USB drive", status.Job?.Title);
    }

    [Fact]
    public async Task A_switched_off_Family_Files_status_carries_nothing_but_the_flag_and_still_decodes()
    {
        // Every other field is absent, not null - the models have to treat the two the same.
        using var server = new FakeServer()
            .OnGet("/api/family-files/status", """{"enabled":false,"isAdmin":false,"username":"kid"}""");
        using var client = ClientFor(server);

        var status = await client.FamilyFilesStatusAsync();

        Assert.False(status.Enabled);
        Assert.Null(status.Library);
        Assert.Empty(status.Drives);
        Assert.Null(status.Job);
        Assert.False(status.LibraryOnNetwork);
        Assert.Null(status.LibraryProblem);
    }

    [Fact]
    public async Task A_library_on_another_computer_says_so_and_why_it_cannot_be_reached()
    {
        using var server = new FakeServer().OnGet("/api/family-files/status", """
            {"enabled":true,"isAdmin":true,"username":"morgan","libraryFolder":"smb://192.168.1.20/Family/Files",
             "libraryOnNetwork":true,"libraryProblem":"192.168.1.20 isn't answering."}
            """);
        using var client = ClientFor(server);

        var status = await client.FamilyFilesStatusAsync();

        Assert.Equal("smb://192.168.1.20/Family/Files", status.LibraryFolder);
        Assert.True(status.LibraryOnNetwork);
        Assert.Equal("192.168.1.20 isn't answering.", status.LibraryProblem);
    }

    [Fact]
    public void The_location_form_adds_smb_to_a_bare_computer_and_share_and_keeps_local_folders_as_typed()
    {
        Assert.Equal("smb://nas/Family", FamilyLibraryLocationInput.FromForm(true, " nas/Family ", "", "").Location);
        Assert.Equal("smb://nas/Family", FamilyLibraryLocationInput.FromForm(true, "smb://nas/Family", "", "").Location);
        Assert.Equal(@"\\PC\Family", FamilyLibraryLocationInput.FromForm(true, @"\\PC\Family", "", "").Location);
        Assert.Equal("//nas/Family", FamilyLibraryLocationInput.FromForm(true, "//nas/Family", "", "").Location);

        var network = FamilyLibraryLocationInput.FromForm(true, "nas/Family", " morgan ", "pw");
        Assert.Equal("morgan", network.Username);
        Assert.Equal("pw", network.Password);

        var local = FamilyLibraryLocationInput.FromForm(false, "/mnt/big ", "morgan", "pw");
        Assert.Equal("/mnt/big", local.Location);
        Assert.Equal("", local.Username);
        Assert.Equal("", local.Password);
    }

    [Fact]
    public async Task A_library_location_test_sends_the_form_and_reads_a_working_folder()
    {
        using var server = new FakeServer().On(
            "POST", "/api/family-files/library/test", """{"detail":"Reached smb://nas/Family and wrote a test file there; it is empty."}""");
        using var client = ClientFor(server);

        var result = await client.FamilyFilesTestLibraryLocationAsync(
            new FamilyLibraryLocationInput { Location = "smb://nas/Family", Username = "morgan", Password = "pw" });

        Assert.True(result.Ok);
        Assert.StartsWith("Reached smb://nas/Family", result.Message);
        Assert.Equal("POST /api/family-files/library/test", server.Requests.Single());
        var body = server.Bodies.Single();
        Assert.Contains("\"location\":\"smb://nas/Family\"", body);
        Assert.Contains("\"username\":\"morgan\"", body);
        Assert.Contains("\"password\":\"pw\"", body);
    }

    [Fact]
    public async Task A_refused_library_location_is_an_answer_that_says_a_sign_in_would_help()
    {
        using var server = new FakeServer().On(
            "PUT", "/api/family-files/library", """{"error":"nas turned down the sign-in.","needsLogin":true}""", HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var result = await client.FamilyFilesSetLibraryLocationAsync(new FamilyLibraryLocationInput { Location = "smb://nas/Family" });

        Assert.False(result.Ok);
        Assert.True(result.NeedsLogin);
        Assert.Equal("nas turned down the sign-in.", result.Message);
    }

    [Fact]
    public async Task A_moved_library_reports_the_folder_it_now_uses()
    {
        using var server = new FakeServer().On(
            "PUT", "/api/family-files/library", """{"detail":"The library is now smb://nas/Family.","libraryFolder":"smb://nas/Family"}""");
        using var client = ClientFor(server);

        var result = await client.FamilyFilesSetLibraryLocationAsync(new FamilyLibraryLocationInput { Location = "smb://nas/Family" });

        Assert.True(result.Ok);
        Assert.Equal("smb://nas/Family", result.LibraryFolder);
        Assert.Equal("PUT /api/family-files/library", server.Requests.Single());
    }

    [Fact]
    public async Task A_folder_listing_asks_for_the_path_and_decodes_folders_then_files()
    {
        const string json = """
            {"path":"Photos/2026","name":"2026","parent":"Photos",
             "folders":[{"path":"Photos/2026/Beach","name":"Beach"}],
             "files":[{"path":"Photos/2026/sunset.jpg","name":"sunset.jpg","size":204800,
                       "modifiedUtc":"2026-08-01T18:30:00Z","sha256":"abc","isImage":true,"contentType":"image/jpeg"}]}
            """;
        using var server = new FakeServer().OnGet("/api/family-files/list", json);
        using var client = ClientFor(server);

        var listing = await client.FamilyFilesListAsync("Photos/2026");

        Assert.Equal("GET /api/family-files/list?path=Photos%2F2026", server.Requests.Single());
        Assert.Equal("Photos", listing.Parent);
        Assert.Equal("Beach", Assert.Single(listing.Folders).Name);
        var file = Assert.Single(listing.Files);
        Assert.Equal(204800, file.Size);
        Assert.True(file.IsImage);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 18, 30, 0, TimeSpan.Zero), file.ModifiedUtc);
    }

    [Fact]
    public async Task An_upload_arrives_as_raw_bytes_at_the_library_path_with_the_original_date()
    {
        var local = Path.Combine(Path.GetTempPath(), $"sentinel-upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(local, "hello from the beach");
        try
        {
            // The fake server matches on the URL as it arrives - escaped, which is the point.
            using var server = new FakeServer().On(
                "PUT",
                "/api/family-files/files/Photos/2026%20trip/notes.txt",
                """{"path":"Photos/2026 trip/notes.txt","name":"notes.txt","size":20,"sha256":"x","alreadyThere":false}""");
            using var client = ClientFor(server);

            var sent = new List<long>();
            var result = await client.FamilyFilesUploadAsync(
                local,
                "Photos/2026 trip/notes.txt",
                new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
                new SynchronousProgress(sent));

            // The body is the file itself, not JSON or multipart, and the path travels in the URL
            // with each segment escaped on its own - a space in a folder name has to survive.
            var request = server.Requests.Single();
            Assert.StartsWith("PUT /api/family-files/files/Photos/2026%20trip/notes.txt?modified=", request);
            Assert.Contains("modified=2026-09-25T10%3A00%3A00Z", request);
            Assert.StartsWith("application/octet-stream", server.ContentTypes.Single());
            Assert.Equal("hello from the beach", server.Bodies.Single());
            Assert.Equal("Photos/2026 trip/notes.txt", result.Path);
            Assert.False(result.AlreadyThere);
            Assert.Equal(20, sent.LastOrDefault());
        }
        finally
        {
            File.Delete(local);
        }
    }

    [Fact]
    public async Task A_download_streams_the_file_to_disk_and_reports_the_bytes_as_they_land()
    {
        var payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        var local = Path.Combine(Path.GetTempPath(), "sentinel-download", $"{Guid.NewGuid():N}", "clip.bin");
        try
        {
            using var server = new FakeServer().OnBytes("/api/family-files/files/Videos/clip.bin", payload, "application/octet-stream");
            using var client = ClientFor(server);

            var seen = new List<long>();
            await client.FamilyFilesDownloadAsync("Videos/clip.bin", local, new SynchronousProgress(seen));

            Assert.Equal("GET /api/family-files/files/Videos/clip.bin", server.Requests.Single());
            Assert.Equal(payload, await File.ReadAllBytesAsync(local));
            Assert.Equal(payload.Length, seen.Last());
            // No half-written file left beside the real one.
            Assert.False(File.Exists(local + ".part"));
        }
        finally
        {
            if (Path.GetDirectoryName(local) is { } folder && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_library_file_is_a_server_error_not_a_written_file()
    {
        var local = Path.Combine(Path.GetTempPath(), $"sentinel-missing-{Guid.NewGuid():N}.bin");
        using var server = new FakeServer()
            .OnGet("/api/family-files/files/gone.bin", """{"error":"That file isn't in the library."}""", HttpStatusCode.NotFound);
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<SentinelServerException>(() => client.FamilyFilesDownloadAsync("gone.bin", local));

        Assert.Equal(404, error.Status);
        Assert.Equal("That file isn't in the library.", error.Message);
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task Creating_a_backup_drive_sends_every_field_and_decodes_the_saved_drive()
    {
        using var server = new FakeServer().On(
            "POST",
            "/api/family-files/drives",
            """
            {"id":"d7","name":"NAS","kind":"sftp","enabled":true,"location":"sftp://nas/backups/family",
             "sftpHost":"nas","sftpPort":22,"sftpUsername":"backup","sftpFolder":"/backups/family",
             "sftpHostKeyFingerprint":"","hasPassword":true,"lastResult":"","lastMessage":""}
            """);
        using var client = ClientFor(server);

        var drive = await client.FamilyFilesCreateDriveAsync(new FamilyDriveInput
        {
            Name = "NAS",
            Kind = "sftp",
            SftpHost = "nas",
            SftpUsername = "backup",
            SftpPassword = "secret",
            SftpFolder = "/backups/family",
        });

        Assert.Equal("d7", drive.Id);
        Assert.True(drive.IsSftp);
        Assert.True(drive.HasPassword);
        Assert.Null(drive.LastAttemptUtc);

        // All of DRIVE_INPUT travels, blanks included: the server validates the kind in use and
        // ignores the rest, and a client that dropped a field would fail its validation instead.
        var body = server.Bodies.Single();
        Assert.StartsWith("application/json", server.ContentTypes.Single());
        Assert.Contains("\"sftpPassword\":\"secret\"", body);
        Assert.Contains("\"folderPath\":\"\"", body);
        Assert.Contains("\"sftpPort\":22", body);
        Assert.Contains("\"enabled\":true", body);
    }

    [Fact]
    public async Task A_failed_drive_test_is_an_answer_with_the_host_key_the_server_saw()
    {
        // A 400 here is the test's verdict, not a broken request - and it carries the one thing
        // the edit form needs next: the fingerprint to offer.
        using var server = new FakeServer().On(
            "POST",
            "/api/family-files/drives/test",
            """{"error":"The host key does not match.","hostKeyFingerprint":"SHA256:abc"}""",
            HttpStatusCode.BadRequest);
        using var client = ClientFor(server);

        var result = await client.FamilyFilesTestDriveAsync(new FamilyDriveInput { Name = "NAS", Kind = "sftp" });

        Assert.False(result.Ok);
        Assert.Equal("The host key does not match.", result.Message);
        Assert.Equal("SHA256:abc", result.HostKeyFingerprint);
    }

    [Fact]
    public async Task Library_paths_are_escaped_one_segment_at_a_time()
    {
        Assert.Equal("Q%26A%20%232.pdf", SentinelClient.EscapeLibraryPath("Q&A #2.pdf"));
        Assert.Equal("Photos/2026/a%2Bb.jpg", SentinelClient.EscapeLibraryPath("/Photos/2026/a+b.jpg"));

        using var server = new FakeServer().On("DELETE", "/api/family-files/files/Docs/Q%26A%20%232.pdf", """{"trashed":1,"batchId":"b1"}""");
        using var client = ClientFor(server);

        var result = await client.FamilyFilesDeleteAsync("Docs/Q&A #2.pdf");

        Assert.Equal(1, result.Trashed);
        Assert.Equal("DELETE /api/family-files/files/Docs/Q%26A%20%232.pdf", server.Requests.Single());
    }

    /// <summary>
    /// Progress<T> posts to a SynchronizationContext, which a test has none of - so the reports
    /// would arrive after the assertions. This one records them on the spot.
    /// </summary>
    private sealed class SynchronousProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
