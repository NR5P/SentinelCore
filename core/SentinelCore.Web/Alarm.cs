using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SentinelCore.Web;

// The home alarm: Device Hub sensors (door contacts, motion, leak, smoke) grouped into a system
// that can be armed and disarmed as a whole. This file is the vocabulary and the storage; the
// arm/disarm state machine is AlarmEngine, and AlarmWatcher is the MQTT subscription that feeds
// it sensor readings. Everything lives here in the dashboard process, beside the cameras and the
// push sender it fires, the same way SensorTriggerWatcher does.
//
// A sensor here is a Device Hub device with a role. The device already knows its topics and how
// to read its payload; the alarm only adds *what it means* - a door on the way in, a window, a
// motion sensor in a room nobody should be in when the house is empty, or a leak sensor that
// matters whether the alarm is armed or not.
public static class AlarmModes
{
    public const string Disarmed = "disarmed";
    // Armed with people home: doors and windows watch, motion inside is ignored.
    public const string Home = "home";
    // Home, but for the night - the same sensors as Home. Kept separate so the vision prompt and
    // automation rules can tell "family asleep" from "family in and out", and so a later
    // schedule can move between them.
    public const string Night = "night";
    // Nobody home: everything watches, including motion inside.
    public const string Away = "away";

    public static readonly string[] All = [Disarmed, Home, Night, Away];
    public static readonly string[] Armed = [Home, Night, Away];

    public static bool IsArmedMode(string mode) => Armed.Contains(mode, StringComparer.Ordinal);
}

public static class AlarmStatuses
{
    public const string Disarmed = "disarmed";
    // The exit delay is running: the door can open and close while people leave.
    public const string Arming = "arming";
    public const string Armed = "armed";
    // An entry door opened while armed: the entry delay is running and the siren has not sounded.
    public const string Pending = "pending";
    public const string Triggered = "triggered";
}

public static class AlarmZones
{
    // The way in and out. Opening it while armed starts the entry delay instead of the siren.
    public const string Entry = "entry";
    // Windows, other doors: instant when armed.
    public const string Perimeter = "perimeter";
    // Motion inside. Instant, but only watched in Away by default - people at home walk around.
    public const string Interior = "interior";
    // Water leak, smoke, tamper: watched around the clock, armed or not.
    public const string Always = "always";

    public static readonly string[] All = [Entry, Perimeter, Interior, Always];

    public static bool IsKnown(string zone) => All.Contains(zone, StringComparer.Ordinal);
}

public sealed class AlarmSensor
{
    public Guid Id { get; set; }
    // The Device Hub device that is this sensor. One sensor per device.
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = "";
    public string Zone { get; set; } = AlarmZones.Perimeter;
    // Which armed modes watch this sensor. An "always" sensor ignores these - it is watched in
    // every mode including disarmed.
    public bool ArmedHome { get; set; } = true;
    public bool ArmedNight { get; set; } = true;
    public bool ArmedAway { get; set; } = true;
    // Most sensors say ON for "open"/"motion"/"wet" (the Home Assistant convention the Device Hub
    // reads them with). The odd one reports the other way round; this flips it.
    public bool Inverted { get; set; }
    public bool Enabled { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool WatchedIn(string mode) => Zone == AlarmZones.Always || mode switch
    {
        AlarmModes.Home => ArmedHome,
        AlarmModes.Night => ArmedNight,
        AlarmModes.Away => ArmedAway,
        _ => false
    };

    public static AlarmSensor Defaults(string zone) => new()
    {
        Zone = zone,
        // Motion inside the house only matters when nobody should be in it.
        ArmedHome = zone != AlarmZones.Interior,
        ArmedNight = zone != AlarmZones.Interior,
        ArmedAway = true
    };
}

// The knobs, kept with the alarm rather than in Settings: they are edited from the alarm tab by
// the person standing at it, and a PIN belongs beside the thing it protects.
public sealed class AlarmConfig
{
    // How long people have to leave after arming Away before the sensors watch. Home and Night
    // arm at once - nobody is leaving.
    public int ExitDelaySeconds { get; set; } = 60;
    // How long an entry door may be open before the siren, so whoever came in can disarm.
    public int EntryDelaySeconds { get; set; } = 30;
    // How long the siren devices stay on after a trigger. Disarming turns them off sooner.
    public int SirenSeconds { get; set; } = 300;
    // Device Hub devices switched ON when the alarm triggers (a siren, a strobe, every lamp in
    // the house) and OFF again when it is disarmed or the siren time is up.
    public List<Guid> SirenDeviceIds { get; set; } = [];
    // Cameras to snapshot through the AI pipeline when the alarm triggers. Empty = all of them.
    public List<Guid> CameraIds { get; set; } = [];
    public bool NotifyPush { get; set; } = true;
    // Also tell everyone's phones about ordinary arms and disarms, not only triggers.
    public bool NotifyArmDisarm { get; set; } = true;
    // A sensor at or below this battery percentage is flagged and everyone is told once.
    public int LowBatteryPercent { get; set; } = 20;
    // A sensor that has said nothing for this long is treated as offline. Battery sensors check
    // in at least hourly, so a day of silence means the battery is dead or it fell off the mesh.
    public int OfflineAfterHours { get; set; } = 24;
    // PasswordHasher output; null means no PIN, and anyone signed in can disarm.
    public string? PinHash { get; set; }
    public bool RequirePinToArm { get; set; }

    public bool HasPin => !string.IsNullOrEmpty(PinHash);
}

// What survives a restart: which mode the house is in. Timers restart from their remembered
// deadlines, so a restart during the exit delay doesn't grant a fresh minute.
public sealed class AlarmState
{
    public string Mode { get; set; } = AlarmModes.Disarmed;
    public string Status { get; set; } = AlarmStatuses.Disarmed;
    // Null until the first arm or disarm.
    public DateTimeOffset? ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public DateTimeOffset? ExitUntil { get; set; }
    public DateTimeOffset? PendingUntil { get; set; }
    public DateTimeOffset? TriggeredAt { get; set; }
    public string? TriggeredBy { get; set; }
    public DateTimeOffset? SirenUntil { get; set; }
    // Sensors that were open when the alarm was armed with "arm anyway": ignored until disarm.
    public List<Guid> Bypassed { get; set; } = [];
}

public sealed record AlarmEvent(long Id, DateTimeOffset At, string Kind, string Message, string? Actor, Guid? SensorId);

public static class AlarmEventKinds
{
    public const string Armed = "armed";
    public const string Arming = "arming";
    public const string Disarmed = "disarmed";
    public const string Pending = "pending";
    public const string Triggered = "triggered";
    public const string SensorOpened = "sensorOpened";
    public const string Bypassed = "bypassed";
    public const string BatteryLow = "batteryLow";
    public const string BatteryOk = "batteryOk";
    public const string Offline = "offline";
    public const string Online = "online";
    public const string PinRejected = "pinRejected";
    public const string Test = "test";
    public const string Panic = "panic";
    public const string SensorAdded = "sensorAdded";
    public const string SensorRemoved = "sensorRemoved";
    public const string ConfigChanged = "configChanged";
}

public interface IAlarmStore
{
    Task<IReadOnlyList<AlarmSensor>> GetSensorsAsync(CancellationToken cancellationToken);
    Task SaveSensorAsync(AlarmSensor sensor, CancellationToken cancellationToken);
    Task<bool> DeleteSensorAsync(Guid id, CancellationToken cancellationToken);
    Task<AlarmConfig> GetConfigAsync(CancellationToken cancellationToken);
    Task SaveConfigAsync(AlarmConfig config, CancellationToken cancellationToken);
    Task<AlarmState> GetStateAsync(CancellationToken cancellationToken);
    Task SaveStateAsync(AlarmState state, CancellationToken cancellationToken);
    Task AddEventAsync(string kind, string message, string? actor, Guid? sensorId, DateTimeOffset at, CancellationToken cancellationToken);
    Task<IReadOnlyList<AlarmEvent>> GetEventsAsync(int limit, CancellationToken cancellationToken);
}

// alarm.db in the dashboard's data directory. Sensors and config are configuration and go in
// the backup; events are history and don't.
public sealed class SqliteAlarmStore : IAlarmStore, IDisposable
{
    private const int EventsKept = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAlarmStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "alarm.db")}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS AlarmSensors (Id TEXT PRIMARY KEY, DeviceId TEXT NOT NULL UNIQUE, Definition TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AlarmSettings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AlarmEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, Kind TEXT NOT NULL, Message TEXT NOT NULL,
                Actor TEXT NULL, SensorId TEXT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<IReadOnlyList<AlarmSensor>> GetSensorsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var list = new List<AlarmSensor>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Definition FROM AlarmSensors;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sensor = Read<AlarmSensor>(reader.GetString(0));
                if (sensor is not null) list.Add(sensor);
            }
            return [.. list.OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSensorAsync(AlarmSensor sensor, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlarmSensors (Id, DeviceId, Definition) VALUES ($id, $device, $json)
                ON CONFLICT(Id) DO UPDATE SET DeviceId = excluded.DeviceId, Definition = excluded.Definition;
                """;
            command.Parameters.AddWithValue("$id", sensor.Id.ToString("N"));
            command.Parameters.AddWithValue("$device", sensor.DeviceId.ToString("N"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(sensor, Json));
            command.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteSensorAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AlarmSensors WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AlarmConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        await GetValueAsync<AlarmConfig>("config", cancellationToken) ?? new AlarmConfig();

    public Task SaveConfigAsync(AlarmConfig config, CancellationToken cancellationToken) =>
        SetValueAsync("config", config, cancellationToken);

    public async Task<AlarmState> GetStateAsync(CancellationToken cancellationToken) =>
        await GetValueAsync<AlarmState>("state", cancellationToken) ?? new AlarmState();

    public Task SaveStateAsync(AlarmState state, CancellationToken cancellationToken) =>
        SetValueAsync("state", state, cancellationToken);

    public async Task AddEventAsync(string kind, string message, string? actor, Guid? sensorId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlarmEvents (At, Kind, Message, Actor, SensorId) VALUES ($at, $kind, $message, $actor, $sensor);
                DELETE FROM AlarmEvents WHERE Id <= (SELECT MAX(Id) FROM AlarmEvents) - $kept;
                """;
            command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
            command.Parameters.AddWithValue("$sensor", sensorId is { } id ? id.ToString("N") : DBNull.Value);
            command.Parameters.AddWithValue("$kept", EventsKept);
            command.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AlarmEvent>> GetEventsAsync(int limit, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var list = new List<AlarmEvent>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Id, At, Kind, Message, Actor, SensorId FROM AlarmEvents ORDER BY Id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, EventsKept));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AlarmEvent(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : Guid.ParseExact(reader.GetString(5), "N")));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<T?> GetValueAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT Value FROM AlarmSettings WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() is string json ? Read<T>(json) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SetValueAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AlarmSettings (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, Json));
            command.ExecuteNonQuery();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static T? Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
