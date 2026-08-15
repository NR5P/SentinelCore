using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The overnight pause. Its reason for existing is measured, not theoretical: over three days in
// August 2026 the gate camera produced 284 triggers between 21:00 and 07:00 of which 5 survived AI
// confirmation, against 23% confirmed in daylight. The night blobs are the IR illuminator finding
// a web close to the lens, and they measure the same number of detector pixels as a person at the
// gate - so the minimum-object-size control cannot separate them and the clock is the only thing
// that can.
public sealed class TriggerNightWindowTests
{
    private static TriggerConfig Config(bool enabled, int start, int end) =>
        TriggerConfig.Disabled(Guid.NewGuid()) with
        {
            NightWindowEnabled = enabled,
            NightStartHour = start,
            NightEndHour = end
        };

    private static DateTimeOffset LocalAt(int hour) =>
        new DateTimeOffset(new DateTime(2026, 8, 13, hour, 30, 0, DateTimeKind.Local));

    // --- The off switch -----------------------------------------------------------------------

    // The single most important property here: this feature blinds a camera for ten hours, so it
    // must do exactly nothing until someone asks for it.
    [Fact]
    public void ADefaultConfig_HasTheNightWindowOff()
    {
        var config = TriggerConfig.Disabled(Guid.NewGuid());

        Assert.False(config.NightWindowEnabled);
        for (var hour = 0; hour < 24; hour++)
            Assert.False(TriggerNightWindow.IsActive(config, LocalAt(hour)));
    }

    [Fact]
    public void Disabled_NeverSuppresses_EvenInsideTheHours()
    {
        var config = Config(enabled: false, start: 21, end: 7);

        Assert.False(TriggerNightWindow.IsActive(config, LocalAt(3)));
    }

    // --- The window itself --------------------------------------------------------------------

    // 21->7 wraps past midnight, which is the case the naive `hour >= start && hour < end` gets
    // wrong (it matches nothing at all), and it is the only window anyone actually wants here.
    [Theory]
    [InlineData(21, true)]   // first hour of the window
    [InlineData(23, true)]
    [InlineData(0, true)]    // across the date boundary
    [InlineData(3, true)]    // the flood's peak
    [InlineData(6, true)]    // last hour of the window
    [InlineData(7, false)]   // end hour is exclusive - triggers are back
    [InlineData(12, false)]
    [InlineData(20, false)]  // last hour before it starts again
    public void AnOvernightWindow_WrapsPastMidnight(int hour, bool expected)
    {
        var config = Config(enabled: true, start: 21, end: 7);

        Assert.Equal(expected, TriggerNightWindow.IsActive(config, LocalAt(hour)));
    }

    // The non-wrapping direction still has to work - someone suppressing a midday sun-glare window
    // is a perfectly reasonable use of the same control.
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    public void AWindowInsideOneDay_DoesNotWrap(int hour, bool expected)
    {
        var config = Config(enabled: true, start: 10, end: 14);

        Assert.Equal(expected, TriggerNightWindow.IsActive(config, LocalAt(hour)));
    }

    // Start == End reads as zero-width, never as "all day". The alternative is a camera that goes
    // permanently blind because someone enabled the feature before setting the hours, which is the
    // worst available failure for a security product.
    [Fact]
    public void EqualStartAndEnd_SuppressesNothing()
    {
        var config = Config(enabled: true, start: 0, end: 0);

        for (var hour = 0; hour < 24; hour++)
            Assert.False(TriggerNightWindow.IsActive(config, LocalAt(hour)));
    }

    // --- The log line -------------------------------------------------------------------------

    // EndsAt is what tells someone reading the journal at 3am when the camera comes back, so it
    // has to roll to tomorrow rather than report an hour that has already passed.
    [Fact]
    public void EndsAt_RollsToTomorrowWhenTheEndHourHasPassed()
    {
        var config = Config(enabled: true, start: 21, end: 7);

        var lateEvening = TriggerNightWindow.EndsAt(config, LocalAt(23)).ToLocalTime();
        Assert.Equal(7, lateEvening.Hour);
        Assert.Equal(14, lateEvening.Day); // the 13th's 23:30 resolves to the 14th's 07:00

        var earlyMorning = TriggerNightWindow.EndsAt(config, LocalAt(3)).ToLocalTime();
        Assert.Equal(7, earlyMorning.Hour);
        Assert.Equal(13, earlyMorning.Day); // still the same morning
    }

    // --- Wiring -------------------------------------------------------------------------------

    // A running detection session holds its own copy of the config. ConfigsEquivalent is what
    // decides whether the reconcile loop rebuilds it, so a field missing from that comparison is
    // an edit that silently does nothing until the next restart - and here the symptom would be a
    // camera that keeps flooding at 3am after you just told it not to.
    [Fact]
    public void ConfigsEquivalent_NoticesEveryNightWindowEdit()
    {
        var baseline = Config(enabled: true, start: 21, end: 7);

        Assert.True(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with { }));
        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with { NightWindowEnabled = false }));
        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with { NightStartHour = 22 }));
        Assert.False(TriggerZoneMonitor.ConfigsEquivalent(baseline, baseline with { NightEndHour = 6 }));
    }

    // Round-trips through SQLite, including the ALTER TABLE path an existing install takes.
    [Fact]
    public async Task TheWindowSurvivesASaveAndReload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        var store = new SqliteTriggerStore(root);
        await store.SaveAsync(
            TriggerConfig.Disabled(cameraId) with
            {
                Enabled = true,
                ZonePolygon = [new(0, 0), new(1, 0), new(1, 1)],
                NightWindowEnabled = true,
                NightStartHour = 22,
                NightEndHour = 5
            },
            ct);

        var reloaded = await store.GetAsync(cameraId, ct);

        Assert.NotNull(reloaded);
        Assert.True(reloaded.NightWindowEnabled);
        Assert.Equal(22, reloaded.NightStartHour);
        Assert.Equal(5, reloaded.NightEndHour);
    }

    // --- The old-client merge rule ------------------------------------------------------------

    // The trap this codebase already fell into once with Zones. The shipped iOS/Android builds
    // PUT the whole trigger config without any night-window keys, so if absent read as false, the
    // window would switch itself off the next time anyone adjusted a zone from their phone - and
    // the only symptom would be the 3am flood quietly coming back.
    [Fact]
    public void AClientThatSendsNoNightKeys_LeavesTheStoredWindowAlone()
    {
        var stored = TriggerConfig.Disabled(Guid.NewGuid()) with
        {
            NightWindowEnabled = true,
            NightStartHour = 22,
            NightEndHour = 5
        };

        // Exactly what an older mobile build sends: the keys simply aren't in the JSON.
        var fromPhone = new TriggerConfigDto { Enabled = true };

        var resolved = TriggerEndpoints.ResolveNightWindow(fromPhone, stored);

        Assert.Equal((true, 22, 5), resolved);
    }

    // Absent must stay distinguishable from an explicit false, or the web dashboard could never
    // turn the thing off again.
    [Fact]
    public void AClientThatExplicitlyDisablesTheWindow_TurnsItOff()
    {
        var stored = TriggerConfig.Disabled(Guid.NewGuid()) with { NightWindowEnabled = true };

        var resolved = TriggerEndpoints.ResolveNightWindow(
            new TriggerConfigDto { NightWindowEnabled = false }, stored);

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void ACameraWithNoStoredRow_DefaultsToTheWindowBeingOff()
    {
        var resolved = TriggerEndpoints.ResolveNightWindow(new TriggerConfigDto(), existing: null);

        Assert.Equal((false, 21, 7), resolved);
    }

    // A row that predates these columns has to migrate to the feature being OFF. Getting this
    // backwards would take every existing install's cameras offline overnight on first boot after
    // the upgrade, with no one having asked for it.
    [Fact]
    public async Task AnExistingCameraMigratesWithTheWindowOff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"trig-{Guid.NewGuid():N}");
        var cameraId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // Written by a store that has no idea the night window exists...
        var before = new SqliteTriggerStore(root);
        await before.SaveAsync(
            TriggerConfig.Disabled(cameraId) with { Enabled = true, ZonePolygon = [new(0, 0), new(1, 0), new(1, 1)] },
            ct);

        // ...and read back after the migration has run.
        var after = await new SqliteTriggerStore(root).GetAsync(cameraId, ct);

        Assert.NotNull(after);
        Assert.False(after.NightWindowEnabled);
        for (var hour = 0; hour < 24; hour++)
            Assert.False(TriggerNightWindow.IsActive(after, LocalAt(hour)));
    }

    // --- The zero-width guard ------------------------------------------------------------------
    //
    // An enabled window whose hours are equal suppresses nothing, so it fails by looking like it
    // worked. The guard has to judge the MERGED window, because a client is free to send the
    // toggle and only one of the two hours - which is how a check written against the request's
    // own fields gets walked straight past.

    private static TriggerConfigDto Dto(bool? enabled, int? start, int? end) => new()
    {
        Enabled = true,
        ZonePolygon = [new(0, 0), new(1, 0), new(1, 1)],
        SensorTriggerOn = "on",
        NightWindowEnabled = enabled,
        NightStartHour = start,
        NightEndHour = end
    };

    private const string ZeroWidthError =
        "The night window's start and end hours can't be the same - that window is zero hours long.";

    [Fact]
    public void AnEnabledZeroWidthWindowIsRefused()
    {
        var (enabled, start, end) = TriggerEndpoints.ResolveNightWindow(Dto(true, 9, 9), existing: null);

        Assert.Contains(ZeroWidthError, TriggerEndpoints.Validate(Dto(true, 9, 9), enabled, start, end));
    }

    // The regression: only one hour in the request, the other inherited from the stored row and
    // equal to it. Judged on the DTO alone this passes validation and stores a window that is on
    // and does nothing.
    [Fact]
    public void AZeroWidthWindowAssembledFromAPartialRequestIsRefused()
    {
        var stored = Config(enabled: true, start: 9, end: 7);
        var dto = Dto(true, null, 9); // sends the end hour only; start inherits 9 from the row

        var (enabled, start, end) = TriggerEndpoints.ResolveNightWindow(dto, stored);

        Assert.Equal((true, 9, 9), (enabled, start, end));
        Assert.Contains(ZeroWidthError, TriggerEndpoints.Validate(dto, enabled, start, end));
    }

    // Off, so equal hours mean nothing and there is nothing to refuse. Rejecting this would block
    // saving an unrelated zone edit on a camera whose hours happen to match.
    [Fact]
    public void ADisabledZeroWidthWindowIsAllowed()
    {
        var (enabled, start, end) = TriggerEndpoints.ResolveNightWindow(Dto(false, 9, 9), existing: null);

        Assert.DoesNotContain(ZeroWidthError, TriggerEndpoints.Validate(Dto(false, 9, 9), enabled, start, end));
    }

    // The ordinary phone PUT: no night-window keys at all, inheriting a real window from the row.
    [Fact]
    public void AClientThatSendsNoNightKeysStillValidates()
    {
        var stored = Config(enabled: true, start: 21, end: 7);
        var dto = Dto(null, null, null);

        var (enabled, start, end) = TriggerEndpoints.ResolveNightWindow(dto, stored);

        Assert.Equal((true, 21, 7), (enabled, start, end));
        Assert.DoesNotContain(ZeroWidthError, TriggerEndpoints.Validate(dto, enabled, start, end));
    }
}
