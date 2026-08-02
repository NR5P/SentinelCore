using Microsoft.Data.Sqlite;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Several zones per camera, each with its own minimum object size. The reason this exists at all
// is perspective: on the gate camera a person at the gate measures ~161px on the 320x180 detector
// grid while the same person in the near driveway is several times that, so one threshold across
// the whole frame is always wrong somewhere - too low and close-range noise floods the tab, too
// high and anyone at the gate is invisible.
public sealed class MultiZoneTriggerTests
{
    private static SqliteTriggerStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        return new SqliteTriggerStore(root);
    }

    private static List<TriggerPoint> Square(double x0, double y0, double x1, double y1) =>
        [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    // --- Upgrading an existing install --------------------------------------------------------

    // The whole migration story: a row written before multi-zone has Zones NULL, and has to keep
    // detecting exactly what it detected yesterday rather than reading as "no zones configured".
    [Fact]
    public async Task ExistingConfigFromBeforeMultiZone_ReadsAsASingleZoneWithItsOldThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var cameraId = Guid.NewGuid();

        // A TriggerConfigs table exactly as it shipped originally - six columns, no Zones.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "triggers.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE TriggerConfigs (
                    CameraId TEXT PRIMARY KEY,
                    Enabled INTEGER NOT NULL,
                    ZonePolygon TEXT NOT NULL,
                    MinTriggerArea REAL NOT NULL,
                    CooldownSeconds INTEGER NOT NULL,
                    MaxTriggersPerMinute INTEGER NOT NULL
                );
                INSERT INTO TriggerConfigs (CameraId, Enabled, ZonePolygon, MinTriggerArea, CooldownSeconds, MaxTriggersPerMinute)
                VALUES ($cameraId, 1, '[{"X":0.1,"Y":0.1},{"X":0.9,"Y":0.1},{"X":0.9,"Y":0.9},{"X":0.1,"Y":0.9}]', 0.0039, 5, 3);
                """;
            command.Parameters.AddWithValue("$cameraId", cameraId.ToString("N"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var store = new SqliteTriggerStore(root);
        var config = await ((ITriggerConfigStore)store).GetAsync(cameraId, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Null(config.Zones); // nothing was invented on disk...
        var zones = config.ResolvedZones; // ...but consumers still see one zone
        Assert.Single(zones);
        Assert.Equal(TriggerConfig.DefaultZoneName, zones[0].Name);
        Assert.Equal(0.0039, zones[0].MinTriggerArea);
        Assert.Equal(4, zones[0].Polygon.Count);
        Assert.Equal(0.1, zones[0].Polygon[0].X);
    }

    // --- Round-tripping the list ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_RoundTripsEveryZoneWithItsOwnThreshold()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        List<TriggerZone> zones =
        [
            new("Gate", Square(0.4, 0.5, 0.8, 0.7), 0.0015),
            new("Driveway", Square(0.2, 0.7, 0.9, 0.95), 0.0090),
            new("Road", Square(0.0, 0.45, 1.0, 0.55), 0.0300)
        ];

        await ((ITriggerConfigStore)store).SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, Zones = zones }, CancellationToken.None);

        var reloaded = await ((ITriggerConfigStore)store).GetAsync(cameraId, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded.ResolvedZones.Count);
        Assert.Equal(["Gate", "Driveway", "Road"], reloaded.ResolvedZones.Select(z => z.Name));
        Assert.Equal(0.0090, reloaded.ResolvedZones[1].MinTriggerArea);
        Assert.Equal(4, reloaded.ResolvedZones[2].Polygon.Count);
    }

    // The legacy columns aren't dead weight - they're what the shipped iOS/Android builds read, so
    // they have to keep describing the primary zone after a multi-zone save.
    [Fact]
    public async Task SaveAsync_MirrorsThePrimaryZoneIntoTheLegacyColumns()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        List<TriggerZone> zones =
        [
            new("Gate", Square(0.4, 0.5, 0.8, 0.7), 0.0015),
            new("Driveway", Square(0.2, 0.7, 0.9, 0.95), 0.0090)
        ];

        await ((ITriggerConfigStore)store).SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, Zones = zones }, CancellationToken.None);

        var reloaded = await ((ITriggerConfigStore)store).GetAsync(cameraId, CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(0.0015, reloaded.MinTriggerArea);
        Assert.Equal(0.4, reloaded.ZonePolygon[0].X);
        Assert.Equal(0.5, reloaded.ZonePolygon[0].Y);
    }

    // --- The rule that stops a phone wiping your zones -----------------------------------------

    // An older mobile build has no idea zones exist and PUTs a body with no `zones` key at all.
    // Treating that as "zones = none" would delete every zone but the first the moment someone
    // nudged the polygon from their phone.
    [Fact]
    public async Task PutFromAnOlderClient_UpdatesThePrimaryZoneAndKeepsTheRest()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        List<TriggerZone> existing =
        [
            new("Gate", Square(0.4, 0.5, 0.8, 0.7), 0.0015),
            new("Driveway", Square(0.2, 0.7, 0.9, 0.95), 0.0090)
        ];
        await ((ITriggerConfigStore)store).SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, Zones = existing }, CancellationToken.None);

        // Exactly what an old build sends: legacy fields only, Zones never set.
        var dto = new TriggerConfigDto
        {
            Enabled = true,
            ZonePolygon = Square(0.35, 0.45, 0.85, 0.75),
            MinTriggerArea = 0.0020
        };
        Assert.Null(dto.Zones);

        var resolved = await TriggerEndpoints.ResolveZonesAsync(cameraId, dto, store, CancellationToken.None);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("Gate", resolved[0].Name);
        Assert.Equal(0.0020, resolved[0].MinTriggerArea);          // primary updated
        Assert.Equal(0.35, resolved[0].Polygon[0].X);
        Assert.Equal("Driveway", resolved[1].Name);                // and the rest survived
        Assert.Equal(0.0090, resolved[1].MinTriggerArea);
    }

    [Fact]
    public async Task PutFromTheNewEditor_ReplacesTheWholeList()
    {
        using var store = NewStore(out _);
        var cameraId = Guid.NewGuid();

        await ((ITriggerConfigStore)store).SaveAsync(
            TriggerConfig.Disabled(cameraId) with
            {
                Enabled = true,
                Zones = [new("Gate", Square(0.4, 0.5, 0.8, 0.7), 0.0015), new("Driveway", Square(0.2, 0.7, 0.9, 0.95), 0.0090)]
            },
            CancellationToken.None);

        var dto = new TriggerConfigDto
        {
            Enabled = true,
            ZonePolygon = Square(0.4, 0.5, 0.8, 0.7),
            MinTriggerArea = 0.0015,
            Zones = [new TriggerZoneDto { Name = "Gate only", Polygon = Square(0.4, 0.5, 0.8, 0.7), MinTriggerArea = 0.0015 }]
        };

        var resolved = await TriggerEndpoints.ResolveZonesAsync(cameraId, dto, store, CancellationToken.None);

        Assert.Single(resolved);
        Assert.Equal("Gate only", resolved[0].Name);
    }

    // --- Detection actually honouring per-zone thresholds --------------------------------------

    // Two zones, same object size, different floors: the point of the whole feature is that the
    // same blob is a trigger in the sensitive zone and ignored in the strict one.
    [Fact]
    public void ProcessFrame_ScoresEachZoneSeparately()
    {
        // Left half and right half of the frame.
        var detector = new MotionDetector(
            [
                new TriggerZone("left", Square(0.0, 0.0, 0.5, 1.0), 0.0015),
                new TriggerZone("right", Square(0.5, 0.0, 1.0, 1.0), 0.0015)
            ],
            shadowSensitivity: 0);

        var blank = new byte[MotionDetector.FrameSizeBytes];
        Assert.Null(detector.ProcessFrame(blank)); // first frame: nothing to diff against

        // A bright 40x40 block wholly inside the LEFT zone.
        var moved = new byte[MotionDetector.FrameSizeBytes];
        FillBlock(moved, x0: 20, y0: 20, size: 40);

        var fractions = detector.ProcessFrame(moved);

        Assert.NotNull(fractions);
        Assert.Equal(2, fractions.Length);

        var expected = 40.0 * 40.0 / MotionDetector.PixelCount;
        Assert.Equal(expected, fractions[0], 4);   // left saw the whole block
        Assert.Equal(0.0, fractions[1]);           // right saw nothing
    }

    [Fact]
    public void ProcessFrame_ObjectBelowAZonesOwnFloor_IsNotATriggerForThatZone()
    {
        var detector = new MotionDetector(
            [
                new TriggerZone("sensitive", Square(0.0, 0.0, 1.0, 1.0), 0.0015), // 86px of 57600
                new TriggerZone("strict", Square(0.0, 0.0, 1.0, 1.0), 0.0300)     // 1728px
            ],
            shadowSensitivity: 0);

        detector.ProcessFrame(new byte[MotionDetector.FrameSizeBytes]);

        // 20x20 = 400px: over the sensitive floor, well under the strict one.
        var moved = new byte[MotionDetector.FrameSizeBytes];
        FillBlock(moved, x0: 100, y0: 60, size: 20);

        var fractions = detector.ProcessFrame(moved);

        Assert.NotNull(fractions);
        Assert.True(fractions[0] >= 0.0015, $"sensitive zone should have fired, got {fractions[0]}");
        Assert.True(fractions[1] < 0.0300, $"strict zone should not have fired, got {fractions[1]}");
    }

    // --- Edits actually taking effect ----------------------------------------------------------

    // ConfigsEquivalent is what decides whether the reconcile loop rebuilds the ffmpeg/detector
    // session. A zone field it doesn't look at is an edit that silently does nothing until the
    // service is restarted - the worst kind of bug, because the UI says it saved.
    [Fact]
    public void ConfigsEquivalent_NoticesEveryKindOfZoneEdit()
    {
        var cameraId = Guid.NewGuid();
        var baseline = TriggerConfig.Disabled(cameraId) with
        {
            Enabled = true,
            Zones = [new("Gate", Square(0.4, 0.5, 0.8, 0.7), 0.0015), new("Driveway", Square(0.2, 0.7, 0.9, 0.95), 0.0090)]
        };

        Assert.True(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with { }));

        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with
        {
            Zones = [.. baseline.ResolvedZones.Take(1)]
        }), "a removed zone");

        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with
        {
            Zones = [baseline.ResolvedZones[0] with { MinTriggerArea = 0.0016 }, baseline.ResolvedZones[1]]
        }), "a changed threshold");

        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with
        {
            Zones = [baseline.ResolvedZones[0] with { Polygon = Square(0.41, 0.5, 0.8, 0.7) }, baseline.ResolvedZones[1]]
        }), "a moved point");

        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with
        {
            Zones = [baseline.ResolvedZones[0] with { Name = "Front gate" }, baseline.ResolvedZones[1]]
        }), "a renamed zone");
    }

    // A legacy config and the same config after migration describe the same detection, so the
    // reconcile loop must not thrash the ffmpeg session on every pass after an upgrade.
    [Fact]
    public void ConfigsEquivalent_LegacyConfigMatchesItsMigratedSelf()
    {
        var cameraId = Guid.NewGuid();
        var polygon = Square(0.4, 0.5, 0.8, 0.7);

        var legacy = TriggerConfig.Disabled(cameraId) with { Enabled = true, ZonePolygon = polygon, MinTriggerArea = 0.0015 };
        var migrated = legacy with { Zones = [new(TriggerConfig.DefaultZoneName, polygon, 0.0015)] };

        Assert.True(TriggerZoneMonitor.ConfigsEquivalent(legacy, migrated));
    }

    // Paints an opaque white square into an otherwise black RGB24 frame.
    private static void FillBlock(byte[] frame, int x0, int y0, int size)
    {
        for (var y = y0; y < y0 + size; y++)
            for (var x = x0; x < x0 + size; x++)
            {
                var offset = (y * MotionDetector.GridWidth + x) * 3;
                frame[offset] = 255;
                frame[offset + 1] = 255;
                frame[offset + 2] = 255;
            }
    }
}
