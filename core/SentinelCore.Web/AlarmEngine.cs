using System.Collections.Concurrent;
using SentinelCore;

namespace SentinelCore.Web;

// What the alarm does to the outside world when something happens. Behind an interface so the
// state machine can be tested with nothing but a clock: no broker, no phones, no cameras.
public interface IAlarmActions
{
    // Null when the command went out; otherwise why it didn't, for the event log.
    Task<string?> SetDeviceAsync(Guid deviceId, bool on, CancellationToken cancellationToken);
    Task PushAsync(string title, string body, CancellationToken cancellationToken);
    // Empty ids = every camera. The source is the trigger's provenance in the Triggers tab.
    Task TriggerCamerasAsync(IReadOnlyList<Guid> cameraIds, string source, CancellationToken cancellationToken);
}

// The latest word from a sensor's device. Open is null until the first reading that said one way
// or the other - a battery report alone doesn't tell us whether the door is open.
public sealed record AlarmSensorLive(
    bool? Open,
    bool Available,
    double? Battery,
    bool BatteryLowFlag,
    double? LinkQuality,
    DateTimeOffset? LastSeen,
    DateTimeOffset? LastChanged)
{
    public static readonly AlarmSensorLive Unknown = new(null, true, null, false, null, null, null);
}

public sealed record AlarmCommandResult(bool Ok, string? Error = null, IReadOnlyList<string>? OpenSensors = null, int StatusCode = 400)
{
    public static readonly AlarmCommandResult Done = new(true, StatusCode: 200);
    public static AlarmCommandResult Refused(string error, int statusCode = 400) => new(false, error, StatusCode: statusCode);
}

public sealed record AlarmSensorView(
    Guid Id,
    Guid DeviceId,
    string Name,
    string Zone,
    bool ArmedHome,
    bool ArmedNight,
    bool ArmedAway,
    bool Inverted,
    bool Enabled,
    int SortOrder,
    string? DeviceName,
    string? Room,
    bool DeviceMissing,
    // open | closed | unknown
    string State,
    bool Available,
    bool Offline,
    double? Battery,
    bool LowBattery,
    double? LinkQuality,
    DateTimeOffset? LastSeen,
    DateTimeOffset? LastChanged,
    bool Bypassed,
    // Whether this sensor would set the alarm off in the current mode.
    bool Watched);

public sealed record AlarmConfigView(
    int ExitDelaySeconds,
    int EntryDelaySeconds,
    int SirenSeconds,
    IReadOnlyList<Guid> SirenDeviceIds,
    IReadOnlyList<Guid> CameraIds,
    bool NotifyPush,
    bool NotifyArmDisarm,
    int LowBatteryPercent,
    int OfflineAfterHours,
    bool RequirePinToArm);

public sealed record AlarmStatusSnapshot(
    string Mode,
    string Status,
    DateTimeOffset? ChangedAt,
    string? ChangedBy,
    DateTimeOffset? ExitUntil,
    DateTimeOffset? PendingUntil,
    DateTimeOffset? TriggeredAt,
    string? TriggeredBy,
    bool SirenOn,
    bool PinSet,
    bool BrokerConnected,
    // Nothing is open, offline or unread among the sensors an Away arm would watch.
    bool Ready,
    IReadOnlyList<string> Issues,
    IReadOnlyList<AlarmSensorView> Sensors,
    AlarmConfigView Config,
    DateTimeOffset Now);

// The arm/disarm state machine. One instance; every transition runs under one lock so a sensor
// message and a disarm tap arriving together can't both act on the same status. It knows
// nothing about MQTT: AlarmWatcher hands it readings, the endpoints hand it commands, and the
// tick loop hands it the time.
//
// Mode is the house's occupancy - home, night, away - and status is where the machine is within
// that: the exit delay running, armed and quiet, an entry delay counting down, or triggered.
public sealed class AlarmEngine(IAlarmStore store, IAlarmActions actions, TimeProvider clock, ILogger<AlarmEngine> logger)
{
    public const int MaxPinFailures = 5;
    public static readonly TimeSpan PinLockout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestSirenLength = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, AlarmSensorLive> _live = new();
    private readonly HashSet<Guid> _batteryWarned = [];
    private readonly HashSet<Guid> _offlineWarned = [];
    private volatile IReadOnlyDictionary<Guid, HubDevice> _devices = new Dictionary<Guid, HubDevice>();
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    private AlarmState? _state;
    private AlarmConfig? _config;
    private List<AlarmSensor>? _sensors;
    private int _pinFailures;
    private DateTimeOffset? _pinLockedUntil;
    private bool _sirenOn;

    // Set by the watcher; shown on the tab so "why doesn't anything update" has an answer.
    public bool BrokerConnected { get; set; }

    // Raised after a sensor is added, changed or removed, so the watcher can re-subscribe now
    // rather than on its next sweep.
    public event Action? SensorsChanged;

    // Raised with the reason ("Front door opened") once the alarm goes off, after the siren and
    // the push are on their way - the speakers (AudioAnnouncer) pause and say so.
    public event Action<string>? Triggered;

    public void SetDevices(IReadOnlyDictionary<Guid, HubDevice> devices) => _devices = devices;

    public IReadOnlyDictionary<Guid, HubDevice> Devices => _devices;

    // A restart during an entry delay lands on Armed, not Triggered: the person who opened the
    // door has probably disarmed by the time the server is back, and a siren on boot is the
    // worst possible first impression. The exit delay keeps its deadline.
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            if (state.Status == AlarmStatuses.Pending)
            {
                state.Status = AlarmStatuses.Armed;
                state.PendingUntil = null;
                await store.SaveStateAsync(state, cancellationToken);
                await LogAsync(AlarmEventKinds.Armed, "Server restarted during an entry delay; back to armed without triggering", null, null, cancellationToken);
            }
            if (state.Status == AlarmStatuses.Triggered && state.SirenUntil is { } until && until > clock.GetUtcNow())
                _sirenOn = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Guid>> WatchedDeviceIdsAsync(CancellationToken cancellationToken) =>
        [.. (await SensorsAsync(cancellationToken)).Where(s => s.Enabled).Select(s => s.DeviceId)];

    // ---------------- Status ----------------

    public async Task<AlarmStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            var config = await ConfigAsync(cancellationToken);
            var sensors = await SensorsAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var views = sensors.Select(s => View(s, state, config, now)).ToList();

            var issues = new List<string>();
            foreach (var view in views.Where(v => v.Enabled))
            {
                if (view.DeviceMissing) issues.Add($"{view.Name}: its Device Hub device no longer exists");
                else if (view.Offline) issues.Add($"{view.Name}: offline - nothing heard from it{(view.LastSeen is { } seen ? $" since {seen:g} UTC" : " yet")}");
                else if (view.State == "unknown") issues.Add($"{view.Name}: no reading yet");
                if (view.LowBattery) issues.Add($"{view.Name}: battery {(view.Battery is { } b ? $"{b:0}%" : "low")}");
            }
            if (sensors.Count == 0) issues.Add("No sensors yet - add a Device Hub sensor below");
            if (!BrokerConnected && sensors.Count > 0) issues.Add("Not connected to the MQTT broker - sensors can't be heard");

            var ready = sensors.Count > 0 && BrokerConnected && views.Where(v => v.Enabled && v.WatchedInAway(sensors))
                .All(v => v.State == "closed" && !v.Offline && !v.DeviceMissing);

            return new AlarmStatusSnapshot(
                state.Mode, state.Status, state.ChangedAt, state.ChangedBy, state.ExitUntil, state.PendingUntil,
                state.TriggeredAt, state.TriggeredBy, _sirenOn, config.HasPin, BrokerConnected, ready, issues, views,
                ConfigView(config), now);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AlarmSensorView View(AlarmSensor sensor, AlarmState state, AlarmConfig config, DateTimeOffset now)
    {
        var live = _live.GetValueOrDefault(sensor.DeviceId, AlarmSensorLive.Unknown);
        _devices.TryGetValue(sensor.DeviceId, out var device);
        var offline = IsOffline(live, config, now);
        var lowBattery = live.BatteryLowFlag || (live.Battery is { } battery && battery <= config.LowBatteryPercent);
        return new AlarmSensorView(
            sensor.Id, sensor.DeviceId, sensor.Name, sensor.Zone, sensor.ArmedHome, sensor.ArmedNight, sensor.ArmedAway,
            sensor.Inverted, sensor.Enabled, sensor.SortOrder,
            device?.Name, device?.Room, DeviceMissing: device is null,
            live.Open switch { true => "open", false => "closed", null => "unknown" },
            live.Available, offline, live.Battery, lowBattery, live.LinkQuality, live.LastSeen, live.LastChanged,
            state.Bypassed.Contains(sensor.Id),
            Watched: sensor.Enabled && (sensor.Zone == AlarmZones.Always || (AlarmModes.IsArmedMode(state.Mode) && sensor.WatchedIn(state.Mode))));
    }

    private bool IsOffline(AlarmSensorLive live, AlarmConfig config, DateTimeOffset now)
    {
        if (!live.Available) return true;
        var quietFor = now - (live.LastSeen ?? _startedAt);
        return quietFor > TimeSpan.FromHours(Math.Max(1, config.OfflineAfterHours));
    }

    private static AlarmConfigView ConfigView(AlarmConfig config) => new(
        config.ExitDelaySeconds, config.EntryDelaySeconds, config.SirenSeconds, config.SirenDeviceIds, config.CameraIds,
        config.NotifyPush, config.NotifyArmDisarm, config.LowBatteryPercent, config.OfflineAfterHours, config.RequirePinToArm);

    // ---------------- Commands ----------------

    public async Task<AlarmCommandResult> ArmAsync(string mode, bool force, string? pin, string? actor, CancellationToken cancellationToken)
    {
        if (!AlarmModes.IsArmedMode(mode)) return AlarmCommandResult.Refused($"'{mode}' is not an armed mode - use home, night or away.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            var config = await ConfigAsync(cancellationToken);
            var sensors = await SensorsAsync(cancellationToken);
            var now = clock.GetUtcNow();

            if (config.RequirePinToArm && config.HasPin && CheckPin(config, pin, actor, now, cancellationToken) is { } refused) return await refused;
            if (state.Status == AlarmStatuses.Triggered) return AlarmCommandResult.Refused("The alarm is going off - disarm it first.", 409);

            var watched = sensors.Where(s => s.Enabled && s.WatchedIn(mode)).ToList();
            if (watched.Count == 0) return AlarmCommandResult.Refused($"No sensor is set to watch in {mode} mode, so there is nothing to arm.", 409);

            var open = watched.Where(s => _live.GetValueOrDefault(s.DeviceId, AlarmSensorLive.Unknown).Open == true).ToList();
            if (open.Count > 0 && !force)
            {
                return new AlarmCommandResult(false,
                    $"{Names(open)} {(open.Count == 1 ? "is" : "are")} open. Close {(open.Count == 1 ? "it" : "them")}, or arm anyway to skip {(open.Count == 1 ? "it" : "them")} until you disarm.",
                    open.Select(s => s.Name).ToList(), 409);
            }

            await SirenAsync(config, false, cancellationToken);

            var exitDelay = mode == AlarmModes.Away ? Math.Max(0, config.ExitDelaySeconds) : 0;
            state.Mode = mode;
            state.Status = exitDelay > 0 ? AlarmStatuses.Arming : AlarmStatuses.Armed;
            state.ChangedAt = now;
            state.ChangedBy = actor;
            state.ExitUntil = exitDelay > 0 ? now.AddSeconds(exitDelay) : null;
            state.PendingUntil = null;
            state.TriggeredAt = null;
            state.TriggeredBy = null;
            state.SirenUntil = null;
            state.Bypassed = open.Select(s => s.Id).ToList();
            await store.SaveStateAsync(state, cancellationToken);

            foreach (var sensor in open)
                await LogAsync(AlarmEventKinds.Bypassed, $"{sensor.Name} was open and is skipped until the alarm is disarmed", actor, sensor.Id, cancellationToken);

            var who = actor is null ? "" : $" by {actor}";
            if (exitDelay > 0)
                await LogAsync(AlarmEventKinds.Arming, $"Arming {mode}{who} - {exitDelay}s to leave", actor, null, cancellationToken);
            else
                await LogAsync(AlarmEventKinds.Armed, $"Armed {mode}{who}", actor, null, cancellationToken);

            if (config.NotifyPush && config.NotifyArmDisarm)
                Fire(actions.PushAsync("Alarm armed", $"Armed {mode}{who}{(open.Count > 0 ? $", skipping {Names(open)}" : "")}", CancellationToken.None));

            return AlarmCommandResult.Done;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AlarmCommandResult> DisarmAsync(string? pin, string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            var config = await ConfigAsync(cancellationToken);
            var now = clock.GetUtcNow();

            if (config.HasPin && CheckPin(config, pin, actor, now, cancellationToken) is { } refused) return await refused;

            var wasTriggered = state.Status == AlarmStatuses.Triggered;
            var alreadyOff = state.Status == AlarmStatuses.Disarmed;
            await SirenAsync(config, false, cancellationToken);

            state.Mode = AlarmModes.Disarmed;
            state.Status = AlarmStatuses.Disarmed;
            state.ChangedAt = now;
            state.ChangedBy = actor;
            state.ExitUntil = null;
            state.PendingUntil = null;
            state.SirenUntil = null;
            state.Bypassed = [];
            await store.SaveStateAsync(state, cancellationToken);

            if (alreadyOff) return AlarmCommandResult.Done;

            var who = actor is null ? "" : $" by {actor}";
            await LogAsync(AlarmEventKinds.Disarmed, wasTriggered ? $"Alarm silenced and disarmed{who}" : $"Disarmed{who}", actor, null, cancellationToken);
            if (config.NotifyPush && (wasTriggered || config.NotifyArmDisarm))
                Fire(actions.PushAsync(wasTriggered ? "Alarm silenced" : "Alarm disarmed", $"Disarmed{who}", CancellationToken.None));

            return AlarmCommandResult.Done;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A panic button: the siren and the notifications, whatever mode the house is in.
    public async Task<AlarmCommandResult> PanicAsync(string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            if (state.Status == AlarmStatuses.Triggered) return AlarmCommandResult.Done;
            await LogAsync(AlarmEventKinds.Panic, $"Panic button{(actor is null ? "" : $" pressed by {actor}")}", actor, null, cancellationToken);
            await TriggerAsync(state, await ConfigAsync(cancellationToken), "Panic button", null, actor, cancellationToken);
            return AlarmCommandResult.Done;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Everything a trigger does, briefly, without changing the mode: the siren for a few seconds,
    // a push, the cameras. So the setup can be checked before it matters.
    public async Task<AlarmCommandResult> TestAsync(string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await StateAsync(cancellationToken);
            if (state.Status == AlarmStatuses.Triggered) return AlarmCommandResult.Refused("The alarm is going off - disarm it first.", 409);
            var config = await ConfigAsync(cancellationToken);
            await LogAsync(AlarmEventKinds.Test, $"Test run{(actor is null ? "" : $" by {actor}")}", actor, null, cancellationToken);

            var problems = new List<string>();
            foreach (var deviceId in config.SirenDeviceIds)
            {
                var error = await actions.SetDeviceAsync(deviceId, true, cancellationToken);
                if (error is not null) problems.Add($"{DeviceName(deviceId)}: {error}");
            }
            if (config.SirenDeviceIds.Count > 0)
            {
                Fire(Task.Run(async () =>
                {
                    await Task.Delay(TestSirenLength);
                    foreach (var deviceId in config.SirenDeviceIds) await actions.SetDeviceAsync(deviceId, false, CancellationToken.None);
                }));
            }
            if (config.NotifyPush) Fire(actions.PushAsync("Alarm test", $"This is a test of the alarm{(actor is null ? "" : $" by {actor}")}. Nothing is wrong.", CancellationToken.None));
            Fire(actions.TriggerCamerasAsync(config.CameraIds, "alarm test", CancellationToken.None));

            return problems.Count == 0
                ? AlarmCommandResult.Done
                : AlarmCommandResult.Refused($"The test ran, but some siren devices didn't respond: {string.Join("; ", problems)}", 200);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Null when the PIN is fine; otherwise the refusal. Wrong guesses are counted across
    // everyone, and five in a row lock the PIN out for a while - a keypad's rule, applied to the
    // apps.
    private Task<AlarmCommandResult>? CheckPin(AlarmConfig config, string? pin, string? actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_pinLockedUntil is { } lockedUntil && lockedUntil > now)
            return Task.FromResult(AlarmCommandResult.Refused($"Too many wrong PINs - try again in {Math.Ceiling((lockedUntil - now).TotalMinutes)} minute(s).", 429));
        _pinLockedUntil = null;

        if (string.IsNullOrEmpty(pin)) return Task.FromResult(AlarmCommandResult.Refused("Enter the alarm PIN.", 403));
        if (PasswordHasher.Verify(pin, config.PinHash!))
        {
            _pinFailures = 0;
            return null;
        }

        _pinFailures++;
        return Rejected();

        async Task<AlarmCommandResult> Rejected()
        {
            if (_pinFailures >= MaxPinFailures)
            {
                _pinLockedUntil = now + PinLockout;
                _pinFailures = 0;
                await LogAsync(AlarmEventKinds.PinRejected, $"Wrong PIN {MaxPinFailures} times{(actor is null ? "" : $" ({actor})")} - locked out for {PinLockout.TotalMinutes:0} minutes", actor, null, cancellationToken);
                if (config.NotifyPush) Fire(actions.PushAsync("Alarm PIN locked", $"The alarm PIN was entered wrong {MaxPinFailures} times.", CancellationToken.None));
                return AlarmCommandResult.Refused($"Too many wrong PINs - try again in {PinLockout.TotalMinutes:0} minutes.", 429);
            }
            await LogAsync(AlarmEventKinds.PinRejected, $"Wrong PIN{(actor is null ? "" : $" ({actor})")}", actor, null, cancellationToken);
            return AlarmCommandResult.Refused("Wrong PIN.", 403);
        }
    }

    // ---------------- Sensor readings ----------------

    public async Task ObserveAvailabilityAsync(Guid deviceId, bool available, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var previous = _live.GetValueOrDefault(deviceId, AlarmSensorLive.Unknown);
            _live[deviceId] = previous with { Available = available, LastSeen = available ? now : previous.LastSeen };
            await CheckHealthAsync(deviceId, now, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ObserveAsync(Guid deviceId, DeviceStateReading reading, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var sensors = await SensorsAsync(cancellationToken);
            var sensor = sensors.FirstOrDefault(s => s.DeviceId == deviceId);
            var previous = _live.GetValueOrDefault(deviceId, AlarmSensorLive.Unknown);

            bool? open = reading.Power switch
            {
                DevicePower.On => !(sensor?.Inverted ?? false),
                DevicePower.Off => sensor?.Inverted ?? false,
                _ => previous.Open
            };
            var battery = reading.Measurements.TryGetValue("battery", out var b) ? b : previous.Battery;
            var lowFlag = reading.Measurements.TryGetValue("battery_low", out var low) ? low > 0 : previous.BatteryLowFlag;
            var link = reading.Measurements.TryGetValue("linkquality", out var lq) ? lq : previous.LinkQuality;
            var changed = open is not null && open != previous.Open;

            _live[deviceId] = new AlarmSensorLive(
                open, Available: true, battery, lowFlag, link, now, changed ? now : previous.LastChanged);

            if (sensor is null || !sensor.Enabled) return;
            await CheckHealthAsync(deviceId, now, cancellationToken);

            // The first reading is the baseline: brokers replay retained state on subscribe, and
            // a door that was already open when the server started is not a break-in now.
            if (previous.Open is null || !changed) return;
            if (open == true) await OpenedAsync(sensor, now, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenedAsync(AlarmSensor sensor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await StateAsync(cancellationToken);
        var config = await ConfigAsync(cancellationToken);
        if (state.Bypassed.Contains(sensor.Id)) return;

        var verb = Verb(sensor);
        if (sensor.Zone == AlarmZones.Always)
        {
            if (state.Status == AlarmStatuses.Triggered)
            {
                await LogAsync(AlarmEventKinds.SensorOpened, $"{sensor.Name} {verb}", null, sensor.Id, cancellationToken);
                return;
            }
            await TriggerAsync(state, config, $"{sensor.Name} {verb}", sensor, null, cancellationToken);
            return;
        }

        switch (state.Status)
        {
            case AlarmStatuses.Disarmed:
            case AlarmStatuses.Arming:
                return;
            case AlarmStatuses.Triggered:
                if (sensor.WatchedIn(state.Mode))
                    await LogAsync(AlarmEventKinds.SensorOpened, $"{sensor.Name} {verb}", null, sensor.Id, cancellationToken);
                return;
        }

        if (!sensor.WatchedIn(state.Mode)) return;

        if (sensor.Zone == AlarmZones.Entry && config.EntryDelaySeconds > 0)
        {
            if (state.Status == AlarmStatuses.Pending)
            {
                await LogAsync(AlarmEventKinds.SensorOpened, $"{sensor.Name} {verb} during the entry delay", null, sensor.Id, cancellationToken);
                return;
            }
            state.Status = AlarmStatuses.Pending;
            state.PendingUntil = now.AddSeconds(config.EntryDelaySeconds);
            await store.SaveStateAsync(state, cancellationToken);
            await LogAsync(AlarmEventKinds.Pending, $"{sensor.Name} {verb} - {config.EntryDelaySeconds}s to disarm", null, sensor.Id, cancellationToken);
            return;
        }

        await TriggerAsync(state, config, $"{sensor.Name} {verb}", sensor, null, cancellationToken);
    }

    private static string Verb(AlarmSensor sensor) => sensor.Zone switch
    {
        AlarmZones.Interior => "saw movement",
        AlarmZones.Always => "went off",
        _ => "opened"
    };

    private async Task TriggerAsync(AlarmState state, AlarmConfig config, string reason, AlarmSensor? sensor, string? actor, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var wasArmed = AlarmModes.IsArmedMode(state.Mode);
        state.Status = AlarmStatuses.Triggered;
        state.TriggeredAt = now;
        state.TriggeredBy = reason;
        state.PendingUntil = null;
        state.ExitUntil = null;
        state.SirenUntil = config.SirenSeconds > 0 ? now.AddSeconds(config.SirenSeconds) : null;
        await store.SaveStateAsync(state, cancellationToken);
        await LogAsync(AlarmEventKinds.Triggered, $"ALARM: {reason}{(wasArmed ? $" while armed {state.Mode}" : "")}", actor, sensor?.Id, cancellationToken);
        logger.LogWarning("Alarm triggered: {Reason}", reason);

        if (config.SirenSeconds > 0) await SirenAsync(config, true, cancellationToken);
        if (config.NotifyPush)
            Fire(actions.PushAsync("🚨 Alarm", $"{reason}{(wasArmed ? $" while armed {state.Mode}" : "")}. Open the app to see the cameras.", CancellationToken.None));
        Fire(actions.TriggerCamerasAsync(config.CameraIds, $"alarm: {reason}", CancellationToken.None));
        try { Triggered?.Invoke(reason); }
        catch (Exception ex) { logger.LogWarning(ex, "A listener for the alarm going off failed"); }
    }

    private async Task SirenAsync(AlarmConfig config, bool on, CancellationToken cancellationToken)
    {
        if (!on && !_sirenOn) return;
        _sirenOn = on && config.SirenDeviceIds.Count > 0;
        foreach (var deviceId in config.SirenDeviceIds)
        {
            var error = await actions.SetDeviceAsync(deviceId, on, cancellationToken);
            if (error is not null)
                logger.LogWarning("Alarm could not switch {Device} {State}: {Error}", DeviceName(deviceId), on ? "on" : "off", error);
        }
    }

    // Battery and silence, checked whenever a device is heard from and on every tick. Told once
    // per problem: the event log and a push when it starts, an event when it clears.
    private async Task CheckHealthAsync(Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sensors = await SensorsAsync(cancellationToken);
        var sensor = sensors.FirstOrDefault(s => s.DeviceId == deviceId && s.Enabled);
        if (sensor is null) return;
        var config = await ConfigAsync(cancellationToken);
        var live = _live.GetValueOrDefault(deviceId, AlarmSensorLive.Unknown);

        var low = live.BatteryLowFlag || (live.Battery is { } battery && battery <= config.LowBatteryPercent);
        if (low && _batteryWarned.Add(sensor.Id))
        {
            var level = live.Battery is { } b ? $"{b:0}%" : "low";
            await LogAsync(AlarmEventKinds.BatteryLow, $"{sensor.Name} battery {level}", null, sensor.Id, cancellationToken);
            if (config.NotifyPush) Fire(actions.PushAsync("Alarm sensor battery low", $"{sensor.Name} is at {level}. Replace its battery soon.", CancellationToken.None));
        }
        // Only a real recovery clears it, not a reading bouncing around the line.
        else if (!low && live.Battery is { } recovered && recovered > config.LowBatteryPercent + 10 && _batteryWarned.Remove(sensor.Id))
        {
            await LogAsync(AlarmEventKinds.BatteryOk, $"{sensor.Name} battery back to {recovered:0}%", null, sensor.Id, cancellationToken);
        }

        var offline = IsOffline(live, config, now);
        if (offline && _offlineWarned.Add(sensor.Id))
        {
            await LogAsync(AlarmEventKinds.Offline, $"{sensor.Name} is offline", null, sensor.Id, cancellationToken);
            if (config.NotifyPush) Fire(actions.PushAsync("Alarm sensor offline", $"{sensor.Name} has stopped reporting. Check its battery and range.", CancellationToken.None));
        }
        else if (!offline && _offlineWarned.Remove(sensor.Id))
        {
            await LogAsync(AlarmEventKinds.Online, $"{sensor.Name} is back online", null, sensor.Id, cancellationToken);
        }
    }

    // ---------------- Time ----------------

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var state = await StateAsync(cancellationToken);
            var config = await ConfigAsync(cancellationToken);

            if (state.Status == AlarmStatuses.Arming && state.ExitUntil is { } exitUntil && exitUntil <= now)
            {
                state.Status = AlarmStatuses.Armed;
                state.ExitUntil = null;

                // A door still open when the exit delay runs out is a door someone forgot, not
                // a break-in: skip it, and say so, rather than set the siren off behind them.
                var sensors = await SensorsAsync(cancellationToken);
                var stillOpen = sensors
                    .Where(s => s.Enabled && s.WatchedIn(state.Mode) && !state.Bypassed.Contains(s.Id)
                                && _live.GetValueOrDefault(s.DeviceId, AlarmSensorLive.Unknown).Open == true)
                    .ToList();
                state.Bypassed.AddRange(stillOpen.Select(s => s.Id));
                await store.SaveStateAsync(state, cancellationToken);

                await LogAsync(AlarmEventKinds.Armed, $"Armed {state.Mode}", null, null, cancellationToken);
                foreach (var sensor in stillOpen)
                    await LogAsync(AlarmEventKinds.Bypassed, $"{sensor.Name} was still open when the exit delay ended - skipped until disarmed", null, sensor.Id, cancellationToken);
                if (stillOpen.Count > 0 && config.NotifyPush)
                    Fire(actions.PushAsync("Alarm armed with a sensor open", $"{Names(stillOpen)} {(stillOpen.Count == 1 ? "was" : "were")} still open. Not watched until you disarm.", CancellationToken.None));
            }

            if (state.Status == AlarmStatuses.Pending && state.PendingUntil is { } pendingUntil && pendingUntil <= now)
            {
                await TriggerAsync(state, config, "Entry delay ran out without a disarm", null, null, cancellationToken);
            }

            if (_sirenOn && state.SirenUntil is { } sirenUntil && sirenUntil <= now)
            {
                state.SirenUntil = null;
                await store.SaveStateAsync(state, cancellationToken);
                await SirenAsync(config, false, cancellationToken);
            }

            foreach (var sensor in await SensorsAsync(cancellationToken))
            {
                if (sensor.Enabled) await CheckHealthAsync(sensor.DeviceId, now, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- Configuration ----------------

    public async Task<IReadOnlyList<AlarmSensor>> GetSensorsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return [.. await SensorsAsync(cancellationToken)]; }
        finally { _gate.Release(); }
    }

    public async Task<(AlarmSensor? Sensor, string? Error)> SaveSensorAsync(AlarmSensor input, string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sensors = await SensorsAsync(cancellationToken);
            var error = Validate(input, sensors);
            if (error is not null) return (null, error);

            var now = clock.GetUtcNow();
            var existing = sensors.FirstOrDefault(s => s.Id == input.Id);
            var sensor = existing ?? new AlarmSensor { Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id, CreatedAt = now, SortOrder = sensors.Count };
            sensor.DeviceId = input.DeviceId;
            sensor.Name = input.Name.Trim();
            sensor.Zone = input.Zone;
            sensor.ArmedHome = input.ArmedHome;
            sensor.ArmedNight = input.ArmedNight;
            sensor.ArmedAway = input.ArmedAway;
            sensor.Inverted = input.Inverted;
            sensor.Enabled = input.Enabled;
            sensor.UpdatedAt = now;
            await store.SaveSensorAsync(sensor, cancellationToken);
            if (existing is null) sensors.Add(sensor);

            // A re-pointed or re-inverted sensor needs a fresh baseline, same as a new one.
            if (existing is null || existing.DeviceId != sensor.DeviceId) _live.TryRemove(sensor.DeviceId, out _);

            await LogAsync(existing is null ? AlarmEventKinds.SensorAdded : AlarmEventKinds.ConfigChanged,
                existing is null ? $"Sensor added: {sensor.Name} ({sensor.Zone})" : $"Sensor changed: {sensor.Name} ({sensor.Zone})", actor, sensor.Id, cancellationToken);
            SensorsChanged?.Invoke();
            return (sensor, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? Validate(AlarmSensor input, IReadOnlyList<AlarmSensor> existing)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Give the sensor a name.";
        if (input.Name.Trim().Length > 80) return "The name is too long (80 characters at most).";
        if (input.DeviceId == Guid.Empty) return "Pick the Device Hub device this sensor is.";
        if (!AlarmZones.IsKnown(input.Zone)) return $"'{input.Zone}' is not a zone - use entry, perimeter, interior or always.";
        if (existing.Any(s => s.DeviceId == input.DeviceId && s.Id != input.Id)) return "That device is already an alarm sensor.";
        if (input.Zone != AlarmZones.Always && !input.ArmedHome && !input.ArmedNight && !input.ArmedAway)
            return "Pick at least one mode this sensor is watched in, or make it an always-on sensor.";
        return null;
    }

    public async Task<bool> DeleteSensorAsync(Guid id, string? actor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sensors = await SensorsAsync(cancellationToken);
            var sensor = sensors.FirstOrDefault(s => s.Id == id);
            if (sensor is null) return false;
            await store.DeleteSensorAsync(id, cancellationToken);
            sensors.Remove(sensor);
            _live.TryRemove(sensor.DeviceId, out _);
            _batteryWarned.Remove(id);
            _offlineWarned.Remove(id);
            var state = await StateAsync(cancellationToken);
            if (state.Bypassed.Remove(id)) await store.SaveStateAsync(state, cancellationToken);
            await LogAsync(AlarmEventKinds.SensorRemoved, $"Sensor removed: {sensor.Name}", actor, id, cancellationToken);
            SensorsChanged?.Invoke();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sensors = await SensorsAsync(cancellationToken);
            var order = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
            foreach (var sensor in sensors)
            {
                if (!order.TryGetValue(sensor.Id, out var index) || sensor.SortOrder == index) continue;
                sensor.SortOrder = index;
                await store.SaveSensorAsync(sensor, cancellationToken);
            }
            sensors.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : a.CreatedAt.CompareTo(b.CreatedAt));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AlarmConfigView> GetConfigAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return ConfigView(await ConfigAsync(cancellationToken)); }
        finally { _gate.Release(); }
    }

    public async Task<string?> SaveConfigAsync(AlarmConfigView input, string? actor, CancellationToken cancellationToken)
    {
        if (input.ExitDelaySeconds is < 0 or > 600) return "The exit delay must be between 0 and 600 seconds.";
        if (input.EntryDelaySeconds is < 0 or > 600) return "The entry delay must be between 0 and 600 seconds.";
        if (input.SirenSeconds is < 0 or > 3600) return "The siren time must be between 0 and 3600 seconds.";
        if (input.LowBatteryPercent is < 1 or > 90) return "The low battery level must be between 1 and 90 percent.";
        if (input.OfflineAfterHours is < 1 or > 720) return "The offline time must be between 1 and 720 hours.";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ConfigAsync(cancellationToken);
            config.ExitDelaySeconds = input.ExitDelaySeconds;
            config.EntryDelaySeconds = input.EntryDelaySeconds;
            config.SirenSeconds = input.SirenSeconds;
            config.SirenDeviceIds = input.SirenDeviceIds.Distinct().ToList();
            config.CameraIds = input.CameraIds.Distinct().ToList();
            config.NotifyPush = input.NotifyPush;
            config.NotifyArmDisarm = input.NotifyArmDisarm;
            config.LowBatteryPercent = input.LowBatteryPercent;
            config.OfflineAfterHours = input.OfflineAfterHours;
            config.RequirePinToArm = input.RequirePinToArm;
            await store.SaveConfigAsync(config, cancellationToken);
            await LogAsync(AlarmEventKinds.ConfigChanged, "Alarm settings changed", actor, null, cancellationToken);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Empty clears it. Changing an existing PIN needs the current one unless the caller says the
    // person is an admin - the household's admin is who resets a forgotten PIN.
    public async Task<string?> SetPinAsync(string? newPin, string? currentPin, bool isAdmin, string? actor, CancellationToken cancellationToken)
    {
        newPin = newPin?.Trim() ?? "";
        if (newPin.Length > 0 && (newPin.Length is < 4 or > 8 || !newPin.All(char.IsAsciiDigit)))
            return "The PIN must be 4 to 8 digits.";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var config = await ConfigAsync(cancellationToken);
            if (config.HasPin && !isAdmin && (string.IsNullOrEmpty(currentPin) || !PasswordHasher.Verify(currentPin, config.PinHash!)))
                return "The current PIN is wrong.";
            config.PinHash = newPin.Length == 0 ? null : PasswordHasher.Hash(newPin);
            await store.SaveConfigAsync(config, cancellationToken);
            _pinFailures = 0;
            _pinLockedUntil = null;
            await LogAsync(AlarmEventKinds.ConfigChanged, newPin.Length == 0 ? "Alarm PIN removed" : "Alarm PIN changed", actor, null, cancellationToken);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<AlarmEvent>> GetEventsAsync(int limit, CancellationToken cancellationToken) =>
        store.GetEventsAsync(limit, cancellationToken);

    // ---------------- Plumbing ----------------

    private async Task<AlarmState> StateAsync(CancellationToken cancellationToken) => _state ??= await store.GetStateAsync(cancellationToken);
    private async Task<AlarmConfig> ConfigAsync(CancellationToken cancellationToken) => _config ??= await store.GetConfigAsync(cancellationToken);
    private async Task<List<AlarmSensor>> SensorsAsync(CancellationToken cancellationToken) => _sensors ??= [.. await store.GetSensorsAsync(cancellationToken)];

    // After a backup restore wrote straight to the store.
    public void Invalidate()
    {
        _sensors = null;
        _config = null;
        SensorsChanged?.Invoke();
    }

    private Task LogAsync(string kind, string message, string? actor, Guid? sensorId, CancellationToken cancellationToken) =>
        store.AddEventAsync(kind, message, actor, sensorId, clock.GetUtcNow(), cancellationToken);

    private string DeviceName(Guid deviceId) => _devices.TryGetValue(deviceId, out var device) ? device.Name : deviceId.ToString("N")[..8];

    private static string Names(IReadOnlyList<AlarmSensor> sensors) => sensors.Count switch
    {
        1 => sensors[0].Name,
        2 => $"{sensors[0].Name} and {sensors[1].Name}",
        _ => $"{string.Join(", ", sensors.Take(sensors.Count - 1).Select(s => s.Name))} and {sensors[^1].Name}"
    };

    // Fire-and-forget for the slow things - a push round-trip, a camera snapshot - so a sensor
    // message handler never waits on them. Each action already logs its own failures.
    private void Fire(Task task) => _ = task.ContinueWith(
        t => logger.LogWarning(t.Exception, "An alarm action failed"),
        TaskContinuationOptions.OnlyOnFaulted);
}

internal static class AlarmSensorViewExtensions
{
    public static bool WatchedInAway(this AlarmSensorView view, IReadOnlyList<AlarmSensor> sensors) =>
        sensors.FirstOrDefault(s => s.Id == view.Id)?.WatchedIn(AlarmModes.Away) ?? false;
}
