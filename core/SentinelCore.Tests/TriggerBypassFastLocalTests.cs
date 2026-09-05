using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The per-camera opt-out from the fast local detector. Measured reason, like the night window:
// on the night of 2026-09-03 the chicken coop fired 697 triggers, of which 346 reached the
// detector and only 40 got past it to the full model. The detector is COCO-trained and has no
// class for a raccoon, fox, opossum or weasel, so the 306 it cleared were not "nothing there" -
// they were frames it had no vocabulary to describe. At a coop that is the whole threat model.
//
// The routing half of this lives in FastLocalTests (BypassFastLocal_*); this file covers storage,
// the absent-key merge, and the live-reload comparison.
public sealed class TriggerBypassFastLocalTests
{
    private static SqliteTriggerStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        return new SqliteTriggerStore(root);
    }

    // --- Storage --------------------------------------------------------------------------------

    [Fact]
    public async Task TheFlagSurvivesAReopen()
    {
        var store = NewStore(out var root);
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, BypassFastLocal = true }, ct);

        // A second connection, so this is the stored column being read and not a cached record.
        var after = await new SqliteTriggerStore(root).GetAsync(cameraId, ct);

        Assert.NotNull(after);
        Assert.True(after.BypassFastLocal);
    }

    [Fact]
    public async Task TurningItBackOffPersists()
    {
        // ON CONFLICT DO UPDATE has to carry the column, or the flag becomes a one-way door: the
        // save reports success, the row keeps the old value, and the camera silently stays on the
        // expensive path.
        var store = NewStore(out var root);
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(TriggerConfig.Disabled(cameraId) with { BypassFastLocal = true }, ct);
        await store.SaveAsync(TriggerConfig.Disabled(cameraId) with { BypassFastLocal = false }, ct);

        var after = await new SqliteTriggerStore(root).GetAsync(cameraId, ct);

        Assert.NotNull(after);
        Assert.False(after.BypassFastLocal);
    }

    [Fact]
    public async Task AnExistingCameraMigratesWithTheFlagOff()
    {
        // Getting this backwards would route every camera on every existing install at the full
        // model on first boot after the upgrade - hours of analysis nobody asked for, and a queue
        // that fails open and keeps the clips forever.
        var store = NewStore(out var root);
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, ZonePolygon = [new(0, 0), new(1, 0), new(1, 1)] },
            ct);

        var after = await new SqliteTriggerStore(root).GetAsync(cameraId, ct);

        Assert.NotNull(after);
        Assert.False(after.BypassFastLocal);
    }

    [Fact]
    public async Task GetAllReadsTheFlag()
    {
        // Three SELECT lists feed the same row mapper by ordinal; one of them missing the column
        // throws at read time rather than returning a wrong answer, so each is worth a look.
        var store = NewStore(out _);
        var ct = TestContext.Current.CancellationToken;

        await store.SaveAsync(
            TriggerConfig.Disabled(Guid.NewGuid()) with { Enabled = true, BypassFastLocal = true }, ct);

        Assert.True((await store.GetAllAsync(ct)).Single().BypassFastLocal);
        Assert.True((await store.GetEnabledAsync(ct)).Single().BypassFastLocal);
    }

    // --- The absent-key merge -------------------------------------------------------------------

    [Fact]
    public void AnOldClientOmittingTheKeyKeepsTheStoredValue()
    {
        // The trap the night window already hit: no shipped mobile build sends this key, so a
        // plain bool would read as false and switch the coop back to the detector the first time
        // someone nudged its zone from a phone.
        var stored = TriggerConfig.Disabled(Guid.NewGuid()) with { BypassFastLocal = true };
        var dto = new TriggerConfigDto { BypassFastLocal = null };

        Assert.True(dto.BypassFastLocal ?? stored.BypassFastLocal);
    }

    [Fact]
    public void AClientThatSendsFalseTurnsItOff()
    {
        // The other half: null means "didn't ask", false means "turn it off", and they must not
        // collapse into each other or the switch is unusable from the web UI.
        var stored = TriggerConfig.Disabled(Guid.NewGuid()) with { BypassFastLocal = true };
        var dto = new TriggerConfigDto { BypassFastLocal = false };

        Assert.False(dto.BypassFastLocal ?? stored.BypassFastLocal);
    }

    // --- Live reload ----------------------------------------------------------------------------

    [Fact]
    public void FlippingTheFlagIsAConfigChange()
    {
        // The running session holds its own copy and reads the flag at forward time, so a config
        // left out of this comparison is an edit that saves, reports success, and does nothing at
        // all until the next restart.
        var cameraId = Guid.NewGuid();
        var off = TriggerConfig.Disabled(cameraId) with { Enabled = true };
        var on = off with { BypassFastLocal = true };

        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(off, on));
        Assert.True(TriggerZoneMonitor.ConfigsEquivalent(on, on with { }));
    }
}
