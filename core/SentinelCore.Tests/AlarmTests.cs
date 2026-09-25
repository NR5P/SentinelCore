extern alias WebHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SentinelCore.Web;
using Xunit;
using WebProgram = WebHost::Program;

namespace SentinelCore.Tests;

// The alarm's state machine, driven with a hand-turned clock and sensor readings fed straight
// in - no broker, no phones. AlarmWatcher is the only thing left untested here, and it is the
// same MQTT plumbing as SensorTriggerWatcher.
public sealed class AlarmEngineTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        foreach (var directory in _directories)
        {
            try { TempDirectories.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeActions : IAlarmActions
    {
        public List<(Guid Device, bool On)> DeviceCommands { get; } = [];
        public List<(string Title, string Body)> Pushes { get; } = [];
        public List<(IReadOnlyList<Guid> Cameras, string Source)> CameraTriggers { get; } = [];
        public string? DeviceError { get; set; }

        public Task<string?> SetDeviceAsync(Guid deviceId, bool on, CancellationToken cancellationToken)
        {
            DeviceCommands.Add((deviceId, on));
            return Task.FromResult(DeviceError);
        }

        public Task PushAsync(string title, string body, CancellationToken cancellationToken)
        {
            Pushes.Add((title, body));
            return Task.CompletedTask;
        }

        public Task TriggerCamerasAsync(IReadOnlyList<Guid> cameraIds, string source, CancellationToken cancellationToken)
        {
            CameraTriggers.Add((cameraIds, source));
            return Task.CompletedTask;
        }
    }

    private sealed class Bench
    {
        public required string Directory { get; init; }
        public required SqliteAlarmStore Store { get; init; }
        public required ManualTime Clock { get; init; }
        public required FakeActions Actions { get; init; }
        public required AlarmEngine Engine { get; init; }

        public Guid FrontDoor { get; } = Guid.NewGuid();
        public Guid Window { get; } = Guid.NewGuid();
        public Guid Hall { get; } = Guid.NewGuid();
        public Guid Leak { get; } = Guid.NewGuid();
        public Guid Siren { get; } = Guid.NewGuid();

        public async Task<AlarmSensor> AddAsync(Guid deviceId, string name, string zone)
        {
            var input = AlarmSensor.Defaults(zone);
            input.DeviceId = deviceId;
            input.Name = name;
            var (sensor, error) = await Engine.SaveSensorAsync(input, "nathan", Ct);
            Assert.Null(error);
            return sensor!;
        }

        // A reading straight from a door contact: open or closed, plus whatever else it said.
        public Task ReadAsync(Guid deviceId, bool open, double? battery = null)
        {
            var measurements = battery is { } b
                ? new Dictionary<string, double> { ["battery"] = b, ["linkquality"] = 120 }
                : DeviceStateReading.NoMeasurements;
            return Engine.ObserveAsync(deviceId, new DeviceStateReading(open ? DevicePower.On : DevicePower.Off, null, measurements), Ct);
        }

        // Every sensor reports closed once, so the engine has a baseline for each.
        public async Task BaselineAsync(params Guid[] devices)
        {
            foreach (var device in devices) await ReadAsync(device, open: false, battery: 90);
        }

        public async Task<AlarmStatusSnapshot> StatusAsync() => await Engine.GetStatusAsync(Ct);

        public async Task<IReadOnlyList<AlarmEvent>> EventsAsync() => await Engine.GetEventsAsync(100, Ct);

        public async Task ArmedHouseAsync(string mode = AlarmModes.Away)
        {
            await AddAsync(FrontDoor, "Front door", AlarmZones.Entry);
            await AddAsync(Window, "Kitchen window", AlarmZones.Perimeter);
            await AddAsync(Hall, "Hall motion", AlarmZones.Interior);
            await AddAsync(Leak, "Utility leak", AlarmZones.Always);
            await BaselineAsync(FrontDoor, Window, Hall, Leak);
            Engine.BrokerConnected = true;
            var result = await Engine.ArmAsync(mode, force: false, pin: null, "nathan", Ct);
            Assert.True(result.Ok, result.Error);
            if (mode == AlarmModes.Away)
            {
                Clock.Advance(TimeSpan.FromSeconds(61));
                await Engine.TickAsync(Ct);
            }
            Assert.Equal(AlarmStatuses.Armed, (await StatusAsync()).Status);
        }
    }

    private Bench NewBench(DateTimeOffset? now = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"alarm-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        var store = new SqliteAlarmStore(directory);
        _disposables.Add(store);
        var clock = new ManualTime(now ?? new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var actions = new FakeActions();
        var engine = new AlarmEngine(store, actions, clock, NullLogger<AlarmEngine>.Instance);
        var bench = new Bench { Directory = directory, Store = store, Clock = clock, Actions = actions, Engine = engine };
        // The Device Hub's side of things, as the watcher would have fetched it.
        engine.SetDevices(new Dictionary<Guid, HubDevice>
        {
            [bench.FrontDoor] = Device(bench.FrontDoor, "Front door contact", "sensor"),
            [bench.Window] = Device(bench.Window, "Kitchen window contact", "sensor"),
            [bench.Hall] = Device(bench.Hall, "Hall PIR", "sensor"),
            [bench.Leak] = Device(bench.Leak, "Utility leak sensor", "sensor"),
            [bench.Siren] = Device(bench.Siren, "Siren", "switch"),
        });
        return bench;
    }

    private static HubDevice Device(Guid id, string name, string kind) => new(
        id, name, kind, "Downstairs", true, kind == "sensor" ? "" : $"cmnd/{id:N}/POWER", $"stat/{id:N}", "", "ON", "OFF", "", false,
        "", "", DeviceTopics.SchemaRaw, "", false, false, "", "", 255, DeviceTopics.SourceManual, 0,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ArmingAway_RunsTheExitDelay_ThenArms()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.FrontDoor, "Front door", AlarmZones.Entry);
        await bench.BaselineAsync(bench.FrontDoor);

        var result = await bench.Engine.ArmAsync(AlarmModes.Away, false, null, "nathan", Ct);
        Assert.True(result.Ok);

        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Arming, status.Status);
        Assert.Equal(AlarmModes.Away, status.Mode);
        Assert.Equal(bench.Clock.Now.AddSeconds(60), status.ExitUntil);

        // The door opening and closing on the way out is not a trigger.
        await bench.ReadAsync(bench.FrontDoor, open: true);
        await bench.ReadAsync(bench.FrontDoor, open: false);
        Assert.Equal(AlarmStatuses.Arming, (await bench.StatusAsync()).Status);

        bench.Clock.Advance(TimeSpan.FromSeconds(59));
        await bench.Engine.TickAsync(Ct);
        Assert.Equal(AlarmStatuses.Arming, (await bench.StatusAsync()).Status);

        bench.Clock.Advance(TimeSpan.FromSeconds(2));
        await bench.Engine.TickAsync(Ct);
        status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Armed, status.Status);
        Assert.Null(status.ExitUntil);
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.Armed);
        Assert.Contains(bench.Actions.Pushes, p => p.Title == "Alarm armed" && p.Body.Contains("nathan"));
    }

    [Fact]
    public async Task ArmingHome_IsImmediate_AndIgnoresInteriorMotion()
    {
        var bench = NewBench();
        await bench.ArmedHouseAsync(AlarmModes.Home);

        await bench.ReadAsync(bench.Hall, open: true);
        Assert.Equal(AlarmStatuses.Armed, (await bench.StatusAsync()).Status);

        // But a window is a window.
        await bench.ReadAsync(bench.Window, open: true);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.Equal("Kitchen window opened", status.TriggeredBy);
    }

    [Fact]
    public async Task EntryDoor_StartsTheEntryDelay_AndDisarmingInTimeStopsIt()
    {
        var bench = NewBench();
        await bench.ArmedHouseAsync();

        await bench.ReadAsync(bench.FrontDoor, open: true);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Pending, status.Status);
        Assert.Equal(bench.Clock.Now.AddSeconds(30), status.PendingUntil);
        Assert.Empty(bench.Actions.CameraTriggers);

        bench.Clock.Advance(TimeSpan.FromSeconds(20));
        await bench.Engine.TickAsync(Ct);
        var disarm = await bench.Engine.DisarmAsync(null, "nathan", Ct);
        Assert.True(disarm.Ok);

        bench.Clock.Advance(TimeSpan.FromSeconds(20));
        await bench.Engine.TickAsync(Ct);
        status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Disarmed, status.Status);
        Assert.Equal(AlarmModes.Disarmed, status.Mode);
        Assert.DoesNotContain(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.Triggered);
    }

    [Fact]
    public async Task EntryDelayRunningOut_Triggers_WithSirenPushAndCameras()
    {
        var bench = NewBench();
        await bench.Engine.SaveConfigAsync((await bench.Engine.GetConfigAsync(Ct)) with
        {
            SirenDeviceIds = [bench.Siren],
            SirenSeconds = 120,
            CameraIds = [Guid.Parse("11111111-1111-1111-1111-111111111111")]
        }, "nathan", Ct);
        await bench.ArmedHouseAsync();

        await bench.ReadAsync(bench.FrontDoor, open: true);
        bench.Clock.Advance(TimeSpan.FromSeconds(31));
        await bench.Engine.TickAsync(Ct);

        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.True(status.SirenOn);
        Assert.Contains(bench.Actions.DeviceCommands, c => c.Device == bench.Siren && c.On);
        Assert.Contains(bench.Actions.Pushes, p => p.Title.Contains("Alarm") && p.Body.Contains("Entry delay"));
        var trigger = Assert.Single(bench.Actions.CameraTriggers);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), Assert.Single(trigger.Cameras));
        Assert.StartsWith("alarm:", trigger.Source);

        // The siren times out on its own; the alarm stays triggered until someone disarms.
        bench.Clock.Advance(TimeSpan.FromSeconds(121));
        await bench.Engine.TickAsync(Ct);
        status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.False(status.SirenOn);
        Assert.Contains(bench.Actions.DeviceCommands, c => c.Device == bench.Siren && !c.On);

        var disarm = await bench.Engine.DisarmAsync(null, "nathan", Ct);
        Assert.True(disarm.Ok);
        Assert.Contains(bench.Actions.Pushes, p => p.Title == "Alarm silenced");
        Assert.Equal(AlarmStatuses.Disarmed, (await bench.StatusAsync()).Status);
    }

    [Fact]
    public async Task PerimeterAndInterior_TriggerInstantly_WhenAway()
    {
        var bench = NewBench();
        await bench.ArmedHouseAsync();

        await bench.ReadAsync(bench.Hall, open: true);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.Equal("Hall motion saw movement", status.TriggeredBy);
        Assert.Single(bench.Actions.CameraTriggers);

        // A second sensor while triggered is logged, not a second siren start.
        await bench.ReadAsync(bench.Window, open: true);
        Assert.Single(bench.Actions.CameraTriggers);
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.SensorOpened && e.Message.Contains("Kitchen window"));
    }

    [Fact]
    public async Task AlwaysSensors_TriggerEvenWhenDisarmed()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.Leak, "Utility leak", AlarmZones.Always);
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.BaselineAsync(bench.Leak, bench.Window);

        await bench.ReadAsync(bench.Window, open: true);
        Assert.Equal(AlarmStatuses.Disarmed, (await bench.StatusAsync()).Status);

        await bench.ReadAsync(bench.Leak, open: true);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.Equal(AlarmModes.Disarmed, status.Mode);
        Assert.Equal("Utility leak went off", status.TriggeredBy);
        Assert.Contains(bench.Actions.Pushes, p => p.Body.StartsWith("Utility leak went off") && !p.Body.Contains("while armed"));
    }

    [Fact]
    public async Task TheFirstReadingIsABaseline_NeverATrigger()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.AddAsync(bench.Leak, "Utility leak", AlarmZones.Always);
        await bench.BaselineAsync(bench.Window);
        bench.Engine.BrokerConnected = true;
        Assert.True((await bench.Engine.ArmAsync(AlarmModes.Home, false, null, null, Ct)).Ok);

        // A retained "wet" replayed on subscribe describes the past, not a new leak.
        await bench.ReadAsync(bench.Leak, open: true);
        Assert.Equal(AlarmStatuses.Armed, (await bench.StatusAsync()).Status);

        // The next one is real.
        await bench.ReadAsync(bench.Leak, open: false);
        await bench.ReadAsync(bench.Leak, open: true);
        Assert.Equal(AlarmStatuses.Triggered, (await bench.StatusAsync()).Status);
    }

    [Fact]
    public async Task ArmingWithADoorOpen_IsRefused_UnlessForced_WhichBypassesIt()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.FrontDoor, "Front door", AlarmZones.Entry);
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.BaselineAsync(bench.FrontDoor, bench.Window);
        await bench.ReadAsync(bench.Window, open: true);

        var refused = await bench.Engine.ArmAsync(AlarmModes.Home, false, null, "nathan", Ct);
        Assert.False(refused.Ok);
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal(["Kitchen window"], refused.OpenSensors);

        var forced = await bench.Engine.ArmAsync(AlarmModes.Home, true, null, "nathan", Ct);
        Assert.True(forced.Ok);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Armed, status.Status);
        Assert.True(status.Sensors.Single(s => s.Name == "Kitchen window").Bypassed);

        // The bypassed window can close and open again without consequence...
        await bench.ReadAsync(bench.Window, open: false);
        await bench.ReadAsync(bench.Window, open: true);
        Assert.Equal(AlarmStatuses.Armed, (await bench.StatusAsync()).Status);

        // ...and the bypass ends with the disarm.
        Assert.True((await bench.Engine.DisarmAsync(null, "nathan", Ct)).Ok);
        await bench.ReadAsync(bench.Window, open: false);
        Assert.True((await bench.Engine.ArmAsync(AlarmModes.Home, false, null, "nathan", Ct)).Ok);
        Assert.False((await bench.StatusAsync()).Sensors.Single(s => s.Name == "Kitchen window").Bypassed);
    }

    [Fact]
    public async Task ADoorStillOpenWhenTheExitDelayEnds_IsBypassed_NotTriggered()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.FrontDoor, "Front door", AlarmZones.Entry);
        await bench.BaselineAsync(bench.FrontDoor);
        Assert.True((await bench.Engine.ArmAsync(AlarmModes.Away, false, null, "nathan", Ct)).Ok);
        await bench.ReadAsync(bench.FrontDoor, open: true);

        bench.Clock.Advance(TimeSpan.FromSeconds(61));
        await bench.Engine.TickAsync(Ct);

        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Armed, status.Status);
        Assert.True(status.Sensors.Single().Bypassed);
        Assert.Contains(bench.Actions.Pushes, p => p.Title == "Alarm armed with a sensor open");
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.Bypassed);
    }

    [Fact]
    public async Task APin_IsNeededToDisarm_AndFiveWrongGuessesLockItOut()
    {
        var bench = NewBench();
        Assert.Null(await bench.Engine.SetPinAsync("2468", null, isAdmin: true, "nathan", Ct));
        Assert.Equal("The PIN must be 4 to 8 digits.", await bench.Engine.SetPinAsync("12", null, true, "nathan", Ct));
        await bench.ArmedHouseAsync(AlarmModes.Home);
        Assert.True((await bench.StatusAsync()).PinSet);

        var noPin = await bench.Engine.DisarmAsync(null, "kid", Ct);
        Assert.False(noPin.Ok);
        Assert.Equal(403, noPin.StatusCode);

        for (var attempt = 0; attempt < AlarmEngine.MaxPinFailures; attempt++)
        {
            var wrong = await bench.Engine.DisarmAsync("0000", "kid", Ct);
            Assert.False(wrong.Ok);
        }
        var locked = await bench.Engine.DisarmAsync("2468", "kid", Ct);
        Assert.False(locked.Ok);
        Assert.Equal(429, locked.StatusCode);
        Assert.Contains(bench.Actions.Pushes, p => p.Title == "Alarm PIN locked");
        Assert.Equal(AlarmStatuses.Armed, (await bench.StatusAsync()).Status);

        bench.Clock.Advance(AlarmEngine.PinLockout + TimeSpan.FromSeconds(1));
        var right = await bench.Engine.DisarmAsync("2468", "kid", Ct);
        Assert.True(right.Ok, right.Error);
        Assert.Equal(AlarmStatuses.Disarmed, (await bench.StatusAsync()).Status);

        // Arming never needs the PIN unless asked for.
        Assert.True((await bench.Engine.ArmAsync(AlarmModes.Home, false, null, "kid", Ct)).Ok);
        await bench.Engine.DisarmAsync("2468", "kid", Ct);
        await bench.Engine.SaveConfigAsync((await bench.Engine.GetConfigAsync(Ct)) with { RequirePinToArm = true }, "nathan", Ct);
        Assert.False((await bench.Engine.ArmAsync(AlarmModes.Home, false, null, "kid", Ct)).Ok);
        Assert.True((await bench.Engine.ArmAsync(AlarmModes.Home, false, "2468", "kid", Ct)).Ok);

        // A non-admin changing the PIN needs the old one; an admin resets a forgotten one.
        Assert.Equal("The current PIN is wrong.", await bench.Engine.SetPinAsync("1357", "9999", false, "kid", Ct));
        Assert.Null(await bench.Engine.SetPinAsync("1357", "2468", false, "kid", Ct));
        Assert.Null(await bench.Engine.SetPinAsync("", null, true, "nathan", Ct));
        Assert.False((await bench.StatusAsync()).PinSet);
    }

    [Fact]
    public async Task LowBattery_IsFlaggedAndPushedOnce_AndClearsWhenReplaced()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.ReadAsync(bench.Window, open: false, battery: 15);
        await bench.ReadAsync(bench.Window, open: false, battery: 14);

        var status = await bench.StatusAsync();
        var sensor = status.Sensors.Single();
        Assert.True(sensor.LowBattery);
        Assert.Equal(14, sensor.Battery);
        Assert.Equal(120, sensor.LinkQuality);
        Assert.Contains(status.Issues, i => i.Contains("battery 14%"));
        Assert.Single(bench.Actions.Pushes, p => p.Title == "Alarm sensor battery low");

        // A fresh battery clears the flag; hovering just over the line does not.
        await bench.ReadAsync(bench.Window, open: false, battery: 25);
        Assert.DoesNotContain(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.BatteryOk);
        await bench.ReadAsync(bench.Window, open: false, battery: 100);
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.BatteryOk);
        Assert.False((await bench.StatusAsync()).Sensors.Single().LowBattery);
    }

    [Fact]
    public async Task ASilentSensor_GoesOffline_AndReadyReflectsIt()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.BaselineAsync(bench.Window);
        bench.Engine.BrokerConnected = true;
        Assert.True((await bench.StatusAsync()).Ready);

        bench.Clock.Advance(TimeSpan.FromHours(25));
        await bench.Engine.TickAsync(Ct);

        var status = await bench.StatusAsync();
        Assert.False(status.Ready);
        Assert.True(status.Sensors.Single().Offline);
        Assert.Contains(status.Issues, i => i.Contains("offline"));
        Assert.Single(bench.Actions.Pushes, p => p.Title == "Alarm sensor offline");

        await bench.ReadAsync(bench.Window, open: false);
        Assert.True((await bench.StatusAsync()).Ready);
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.Online);

        // An availability topic saying so is believed at once.
        await bench.Engine.ObserveAvailabilityAsync(bench.Window, false, Ct);
        Assert.True((await bench.StatusAsync()).Sensors.Single().Offline);
    }

    [Fact]
    public async Task AnInvertedSensor_ReadsTheOtherWayRound()
    {
        var bench = NewBench();
        var input = AlarmSensor.Defaults(AlarmZones.Perimeter);
        input.DeviceId = bench.Window;
        input.Name = "Odd window";
        input.Inverted = true;
        Assert.Null((await bench.Engine.SaveSensorAsync(input, null, Ct)).Error);

        await bench.ReadAsync(bench.Window, open: true);
        Assert.Equal("closed", (await bench.StatusAsync()).Sensors.Single().State);
        await bench.ReadAsync(bench.Window, open: false);
        Assert.Equal("open", (await bench.StatusAsync()).Sensors.Single().State);
    }

    [Fact]
    public async Task Validation_RefusesDuplicatesUnknownZonesAndUnwatchedSensors()
    {
        var bench = NewBench();
        await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);

        var duplicate = AlarmSensor.Defaults(AlarmZones.Entry);
        duplicate.DeviceId = bench.Window;
        duplicate.Name = "Again";
        Assert.Equal("That device is already an alarm sensor.", (await bench.Engine.SaveSensorAsync(duplicate, null, Ct)).Error);

        var odd = AlarmSensor.Defaults("roof");
        odd.DeviceId = Guid.NewGuid();
        odd.Name = "Roof";
        Assert.Contains("not a zone", (await bench.Engine.SaveSensorAsync(odd, null, Ct)).Error);

        var never = AlarmSensor.Defaults(AlarmZones.Perimeter);
        never.DeviceId = Guid.NewGuid();
        never.Name = "Never";
        never.ArmedHome = never.ArmedNight = never.ArmedAway = false;
        Assert.Contains("at least one mode", (await bench.Engine.SaveSensorAsync(never, null, Ct)).Error);

        Assert.Equal("The exit delay must be between 0 and 600 seconds.",
            await bench.Engine.SaveConfigAsync((await bench.Engine.GetConfigAsync(Ct)) with { ExitDelaySeconds = 601 }, null, Ct));
    }

    [Fact]
    public async Task ArmingWithNothingToWatch_IsRefused()
    {
        var bench = NewBench();
        var result = await bench.Engine.ArmAsync(AlarmModes.Away, false, null, null, Ct);
        Assert.False(result.Ok);
        Assert.Equal(409, result.StatusCode);
        Assert.False((await bench.Engine.ArmAsync("party", false, null, null, Ct)).Ok);
    }

    [Fact]
    public async Task PanicAndTest_DoWhatTheyPromise()
    {
        var bench = NewBench();
        await bench.Engine.SaveConfigAsync((await bench.Engine.GetConfigAsync(Ct)) with { SirenDeviceIds = [bench.Siren] }, null, Ct);

        Assert.True((await bench.Engine.TestAsync("nathan", Ct)).Ok);
        Assert.Equal(AlarmStatuses.Disarmed, (await bench.StatusAsync()).Status);
        Assert.Contains(bench.Actions.Pushes, p => p.Title == "Alarm test");
        Assert.Contains(bench.Actions.DeviceCommands, c => c.Device == bench.Siren && c.On);
        Assert.Single(bench.Actions.CameraTriggers, t => t.Source == "alarm test");

        Assert.True((await bench.Engine.PanicAsync("nathan", Ct)).Ok);
        var status = await bench.StatusAsync();
        Assert.Equal(AlarmStatuses.Triggered, status.Status);
        Assert.Equal("Panic button", status.TriggeredBy);
        Assert.Contains(bench.Actions.Pushes, p => p.Body.StartsWith("Panic button"));
    }

    [Fact]
    public async Task StateSurvivesARestart_ButAnEntryDelayDoesNot()
    {
        var bench = NewBench();
        await bench.ArmedHouseAsync();
        await bench.ReadAsync(bench.FrontDoor, open: true);
        Assert.Equal(AlarmStatuses.Pending, (await bench.StatusAsync()).Status);

        var reborn = new AlarmEngine(bench.Store, bench.Actions, bench.Clock, NullLogger<AlarmEngine>.Instance);
        await reborn.InitializeAsync(Ct);
        var status = await reborn.GetStatusAsync(Ct);
        Assert.Equal(AlarmModes.Away, status.Mode);
        Assert.Equal(AlarmStatuses.Armed, status.Status);
        Assert.Null(status.PendingUntil);
        Assert.Equal(4, status.Sensors.Count);
        Assert.All(status.Sensors, s => Assert.Equal("unknown", s.State));
    }

    [Fact]
    public async Task DeletingASensor_ForgetsItEverywhere()
    {
        var bench = NewBench();
        var sensor = await bench.AddAsync(bench.Window, "Kitchen window", AlarmZones.Perimeter);
        await bench.ReadAsync(bench.Window, open: true, battery: 5);
        Assert.True(await bench.Engine.DeleteSensorAsync(sensor.Id, "nathan", Ct));
        Assert.False(await bench.Engine.DeleteSensorAsync(sensor.Id, "nathan", Ct));
        var status = await bench.StatusAsync();
        Assert.Empty(status.Sensors);
        Assert.DoesNotContain(status.Issues, i => i.Contains("battery"));
        Assert.Contains(await bench.EventsAsync(), e => e.Kind == AlarmEventKinds.SensorRemoved);
    }
}

// The endpoints, through the real host: who may do what, and the shapes clients read.
public sealed class AlarmEndpointTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly string _dataDirectory;

    public AlarmEndpointTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"alarm-endpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        _settingsPath = Path.Combine(_dataDirectory, "appsettings.json");
        File.WriteAllText(_settingsPath, WebHostTestSettings.LoginOffSettings);
    }

    public void Dispose()
    {
        try { TempDirectories.Delete(_dataDirectory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<WebProgram> CreateFactory() => new WebApplicationFactory<WebProgram>()
        .WithWebHostBuilder(builder => builder.UseTestSettings(_settingsPath, _dataDirectory));

    [Fact]
    public async Task Status_IsOnByDefault_AndSaysTheHubIsOff()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/alarm/status", Ct);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("hubEnabled").GetBoolean());
        Assert.True(status.GetProperty("canEdit").GetBoolean());
        Assert.Equal("disarmed", status.GetProperty("mode").GetString());
        Assert.Equal("disarmed", status.GetProperty("status").GetString());
        Assert.False(status.GetProperty("ready").GetBoolean());
        Assert.Empty(status.GetProperty("sensors").EnumerateArray());
        Assert.Contains(status.GetProperty("issues").EnumerateArray(), i => i.GetString()!.Contains("No sensors"));
        Assert.Equal(60, status.GetProperty("config").GetProperty("exitDelaySeconds").GetInt32());
        Assert.Equal(4, status.GetProperty("zones").GetArrayLength());
    }

    [Fact]
    public async Task SensorsRoundTrip_AndArmingRefusesWithNothingToWatch()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var refused = await client.PostAsJsonAsync("/api/alarm/arm", new { mode = "away" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var deviceId = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync("/api/alarm/sensors",
            new { deviceId, name = "Front door", zone = "entry", armedHome = true, armedNight = true, armedAway = true, enabled = true }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sensor = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = sensor.GetProperty("id").GetGuid();

        using var duplicate = await client.PostAsJsonAsync("/api/alarm/sensors", new { deviceId, name = "Again", zone = "entry" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using var updated = await client.PutAsJsonAsync($"/api/alarm/sensors/{id}",
            new { deviceId, name = "Front door", zone = "perimeter", armedHome = true, armedNight = false, armedAway = true, enabled = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/alarm/status", Ct);
        var view = Assert.Single(status.GetProperty("sensors").EnumerateArray());
        Assert.Equal("perimeter", view.GetProperty("zone").GetString());
        Assert.Equal("unknown", view.GetProperty("state").GetString());
        Assert.True(view.GetProperty("deviceMissing").GetBoolean());

        // Arming Home without a reading from the door: allowed (unknown is not open), armed at once.
        using var armed = await client.PostAsJsonAsync("/api/alarm/arm", new { mode = "home" }, Ct);
        Assert.Equal(HttpStatusCode.OK, armed.StatusCode);
        status = await client.GetFromJsonAsync<JsonElement>("/api/alarm/status", Ct);
        Assert.Equal("armed", status.GetProperty("status").GetString());
        Assert.Equal("home", status.GetProperty("mode").GetString());

        using var disarmed = await client.PostAsJsonAsync("/api/alarm/disarm", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, disarmed.StatusCode);

        var events = await client.GetFromJsonAsync<JsonElement>("/api/alarm/events?limit=10", Ct);
        Assert.Contains(events.EnumerateArray(), e => e.GetProperty("kind").GetString() == "armed");

        using var deleted = await client.DeleteAsync($"/api/alarm/sensors/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Config_AndPin_RoundTrip()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var siren = Guid.NewGuid();
        using var saved = await client.PutAsJsonAsync("/api/alarm/config", new
        {
            exitDelaySeconds = 45, entryDelaySeconds = 20, sirenSeconds = 60, sirenDeviceIds = new[] { siren },
            cameraIds = Array.Empty<Guid>(), notifyPush = true, notifyArmDisarm = false, lowBatteryPercent = 25,
            offlineAfterHours = 12, requirePinToArm = false
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var config = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(45, config.GetProperty("exitDelaySeconds").GetInt32());
        Assert.Equal(siren, Assert.Single(config.GetProperty("sirenDeviceIds").EnumerateArray()).GetGuid());

        using var bad = await client.PutAsJsonAsync("/api/alarm/config", new { exitDelaySeconds = -1 }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var pin = await client.PutAsJsonAsync("/api/alarm/pin", new { pin = "1234" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/api/alarm/status", Ct);
        Assert.True(status.GetProperty("pinSet").GetBoolean());
        Assert.False(status.TryGetProperty("pinHash", out _));
        Assert.False(status.GetProperty("config").TryGetProperty("pinHash", out _));

        using var shortPin = await client.PutAsJsonAsync("/api/alarm/pin", new { pin = "12" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, shortPin.StatusCode);
    }

    [Fact]
    public async Task TurningTheAlarmOff_HidesEverythingButStatus()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var off = await client.PutAsJsonAsync("/api/settings/alarm", new { enabled = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/alarm/status", Ct);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.TryGetProperty("sensors", out _));

        using var arm = await client.PostAsJsonAsync("/api/alarm/arm", new { mode = "home" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, arm.StatusCode);
    }

    [Fact]
    public async Task TheBackup_CarriesSensorsAndConfig()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var deviceId = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync("/api/alarm/sensors", new { deviceId, name = "Front door", zone = "entry" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var pin = await client.PutAsJsonAsync("/api/alarm/pin", new { pin = "1234" }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);

        var backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", Ct);
        Assert.Equal(BackupService.CurrentSchemaVersion, backup.GetProperty("schemaVersion").GetInt32());
        var alarm = backup.GetProperty("alarm");
        Assert.Equal(deviceId, Assert.Single(alarm.GetProperty("sensors").EnumerateArray()).GetProperty("deviceId").GetGuid());
        Assert.False(string.IsNullOrEmpty(alarm.GetProperty("config").GetProperty("pinHash").GetString()));
        // Never saved, so the section is absent from the file and comes through null - the same
        // as every other on-by-default section. Saving it once makes it a value.
        Assert.True(backup.GetProperty("settings").TryGetProperty("alarm", out _));
        using var saved = await client.PutAsJsonAsync("/api/settings/alarm", new { enabled = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        backup = await client.GetFromJsonAsync<JsonElement>("/api/backup/export", Ct);
        Assert.True(backup.GetProperty("settings").GetProperty("alarm").GetProperty("enabled").GetBoolean());
    }
}

public sealed class AlarmDiscoveryTests
{
    [Fact]
    public void BuildCandidates_ParsesAZigbee2MqttDoorContact_SoTheAlarmCanReadIt()
    {
        // A real Zigbee2MQTT announcement for an Aqara door sensor: the state is one key of the
        // device's JSON payload, and payload_on is the JSON boolean false (contact false = open).
        const string config = """
            {"device_class":"door","name":"contact","object_id":"front_door_contact","payload_off":true,"payload_on":false,
             "state_topic":"zigbee2mqtt/Front door","value_template":"{{ value_json.contact }}",
             "availability":[{"topic":"zigbee2mqtt/bridge/state"}],"unique_id":"0x00158d0001_contact"}
            """;

        var candidate = Assert.Single(MqttDeviceDiscovery.BuildCandidates(
            new Dictionary<string, string> { ["homeassistant/binary_sensor/0x00158d0001/contact/config"] = config },
            observedTopics: [],
            discoveryPrefix: "homeassistant",
            existingCommandTopics: new HashSet<string>(),
            existingStateTopics: new HashSet<string>()));

        Assert.Equal("sensor", candidate.Kind);
        Assert.Equal(DeviceTopics.SchemaJson, candidate.Schema);
        Assert.Equal("contact", candidate.StateValueTemplate);
        Assert.Equal("false", candidate.PayloadOn);
        Assert.Equal("true", candidate.PayloadOff);

        var device = new HubDevice(
            Guid.NewGuid(), "Front door", candidate.Kind, "", true, candidate.CommandTopic, candidate.StateTopic,
            candidate.AvailabilityTopic, candidate.PayloadOn, candidate.PayloadOff, candidate.PayloadStop, candidate.Momentary,
            "online", "offline", candidate.Schema, candidate.StateValueTemplate, false, candidate.SupportsBrightness,
            candidate.BrightnessCommandTopic, candidate.BrightnessStateTopic, candidate.BrightnessScale, candidate.Source,
            0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var closed = DeviceTopics.ParseState(device, """{"contact":true,"battery":97,"linkquality":132,"voltage":3005}""");
        Assert.NotNull(closed);
        Assert.Equal(DevicePower.Off, closed.Power);
        Assert.Equal(97, closed.Measurements["battery"]);

        var open = DeviceTopics.ParseState(device, """{"contact":false,"battery":97,"linkquality":132}""");
        Assert.Equal(DevicePower.On, open!.Power);

        // A motion sensor is the ordinary way round, and a bare "battery only" update leaves
        // the power unknown rather than guessing.
        Assert.Equal("occupancy", MqttDeviceDiscovery.ValueTemplateKey("{{ value_json.occupancy }}"));
        Assert.Equal("occupancy", MqttDeviceDiscovery.ValueTemplateKey("{{ value_json['occupancy'] }}"));
        Assert.Null(MqttDeviceDiscovery.ValueTemplateKey("{{ value_json.temperature | round(1) }}"));
        var quiet = DeviceTopics.ParseState(device, """{"battery":96}""");
        Assert.Equal(DevicePower.Unknown, quiet!.Power);
    }
}
