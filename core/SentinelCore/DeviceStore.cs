using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// A smart-home device the user can see and switch from the Device Hub tab. Deliberately stored
// as raw MQTT topics/payloads rather than a per-vendor integration: anything that speaks MQTT -
// Zigbee2MQTT, Tasmota, ESPHome, Shelly, a hand-rolled ESP32 - is describable this way, and
// MqttDeviceDiscovery fills these fields in automatically for devices that announce themselves.
// See DeviceTopics for how Schema turns these into an actual command payload, and MqttDeviceBus
// for the subscription that keeps State current.
public sealed record HubDevice(
    Guid Id,
    string Name,
    // One of DeviceTopics.KnownKinds. Everything but "sensor" is commandable; a sensor is
    // display-only and needs no CommandTopic. cover/gate/garage speak open/close (PayloadOn
    // carries the open payload, PayloadOff the close one), lock speaks lock/unlock the same
    // way, and button is a stateless momentary press.
    string Kind,
    string Room,
    bool Enabled,
    string CommandTopic,
    string StateTopic,
    string AvailabilityTopic,
    string PayloadOn,
    string PayloadOff,
    // Optional third command for cover-like devices whose motor can halt mid-travel. Empty
    // means the device has no stop and clients show no Stop control.
    string PayloadStop,
    // A pulse device: one press publishes PayloadOn and that's the whole story - a gate relay
    // wired to pulse, a garage remote, a doorbell. No on/off state is expected back, so a
    // command counts as done the moment it's published. Kind "button" is momentary regardless.
    bool Momentary,
    string PayloadAvailable,
    string PayloadNotAvailable,
    // "raw" (the payload is the bare ON/OFF string, as ESPHome and Tasmota's cmnd/stat topics
    // use) or "json" ({"state":"ON","brightness":254}, as Zigbee2MQTT and Home Assistant's own
    // JSON light schema use). Those two shapes cover every broker device seen in the wild; the
    // per-vendor differences beyond them are handled by StateValueTemplate and BrightnessScale.
    string Schema,
    // For the json schema, which key in the state payload holds the on/off value - "state" for
    // Zigbee2MQTT, "POWER" for Tasmota. Dotted paths ("StatusSTS.POWER") walk nested objects.
    // Ignored by the raw schema.
    string StateValueTemplate,
    // Assume the command worked and update the UI immediately, rather than waiting for the
    // device to echo its new state back. Needed for devices that don't report state at all.
    bool Optimistic,
    bool SupportsBrightness,
    // Empty means brightness rides the main CommandTopic/StateTopic as another JSON key, which
    // is what Zigbee2MQTT and ESPHome do. Tasmota uses a separate cmnd/<x>/Dimmer topic instead.
    string BrightnessCommandTopic,
    string BrightnessStateTopic,
    // What the device's own full-brightness value is - 255 for Home Assistant/Zigbee2MQTT, 100
    // for Tasmota. The API always speaks 0-100 and DeviceTopics scales to this.
    int BrightnessScale,
    // "manual", "homeassistant-discovery", or "topic-sweep" - shown in the UI so it's clear
    // which devices were found automatically. Purely informational.
    string DiscoverySource,
    // Where this device sits when the user drags the list into their own order. New devices go
    // to the end; UpdateOrderAsync rewrites the whole sequence at once.
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IDeviceStore
{
    Task<IReadOnlyList<HubDevice>> GetAllAsync(CancellationToken cancellationToken);

    // What MqttDeviceBus subscribes for - a disabled device costs nothing beyond its row.
    Task<IReadOnlyList<HubDevice>> GetEnabledAsync(CancellationToken cancellationToken);
    Task<HubDevice?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<HubDevice> CreateAsync(HubDevice device, CancellationToken cancellationToken);
    Task<HubDevice?> UpdateAsync(Guid id, HubDevice device, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);

    // Rewrites SortOrder to match the given sequence; devices not named keep their old values
    // (they sort after, since new positions start from 1 and unlisted rows keep larger ones only
    // if they had them - in practice every client sends the complete list).
    Task UpdateOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken);
}

public sealed class SqliteDeviceStore : IDeviceStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteDeviceStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "devices.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Devices (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Room TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                CommandTopic TEXT NOT NULL,
                StateTopic TEXT NOT NULL,
                AvailabilityTopic TEXT NOT NULL,
                PayloadOn TEXT NOT NULL,
                PayloadOff TEXT NOT NULL,
                PayloadStop TEXT NOT NULL DEFAULT '',
                Momentary INTEGER NOT NULL DEFAULT 0,
                PayloadAvailable TEXT NOT NULL,
                PayloadNotAvailable TEXT NOT NULL,
                Schema TEXT NOT NULL,
                StateValueTemplate TEXT NOT NULL,
                Optimistic INTEGER NOT NULL,
                SupportsBrightness INTEGER NOT NULL,
                BrightnessCommandTopic TEXT NOT NULL,
                BrightnessStateTopic TEXT NOT NULL,
                BrightnessScale INTEGER NOT NULL,
                DiscoverySource TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // Databases created before these columns existed. ALTER TABLE ADD COLUMN is the whole
        // SQLite migration story - cheap, idempotent via the PRAGMA check, no table rewrite.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Devices);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) existingColumns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        foreach (var (column, definition) in new[]
                 {
                     ("PayloadStop", "TEXT NOT NULL DEFAULT ''"),
                     ("Momentary", "INTEGER NOT NULL DEFAULT 0"),
                     ("SortOrder", "INTEGER NOT NULL DEFAULT 0")
                 })
        {
            if (existingColumns.Contains(column)) continue;
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Devices ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    // SortOrder first so a dragged arrangement wins; legacy rows (all SortOrder 0) fall back to
    // the old room-then-name order, which is also the tiebreaker among unsorted rows.
    public Task<IReadOnlyList<HubDevice>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM Devices ORDER BY SortOrder, Room COLLATE NOCASE, Name COLLATE NOCASE;", cancellationToken);

    public Task<IReadOnlyList<HubDevice>> GetEnabledAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM Devices WHERE Enabled = 1;", cancellationToken);

    private async Task<IReadOnlyList<HubDevice>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;

            var results = new List<HubDevice>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadDevice(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HubDevice?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM Devices WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadDevice(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HubDevice> CreateAsync(HubDevice device, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // New devices join at the end of the user's arrangement rather than at position 0,
            // which would jump them to the top of a hand-ordered list.
            var toSave = device;
            if (toSave.SortOrder == 0)
            {
                using var max = _connection.CreateCommand();
                max.CommandText = "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Devices;";
                toSave = toSave with { SortOrder = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken)) };
            }

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Devices (
                    Id, Name, Kind, Room, Enabled, CommandTopic, StateTopic, AvailabilityTopic,
                    PayloadOn, PayloadOff, PayloadStop, Momentary, PayloadAvailable, PayloadNotAvailable,
                    Schema, StateValueTemplate,
                    Optimistic, SupportsBrightness, BrightnessCommandTopic, BrightnessStateTopic, BrightnessScale,
                    DiscoverySource, SortOrder, CreatedAt, UpdatedAt
                ) VALUES (
                    $id, $name, $kind, $room, $enabled, $commandTopic, $stateTopic, $availabilityTopic,
                    $payloadOn, $payloadOff, $payloadStop, $momentary, $payloadAvailable, $payloadNotAvailable,
                    $schema, $stateValueTemplate,
                    $optimistic, $supportsBrightness, $brightnessCommandTopic, $brightnessStateTopic, $brightnessScale,
                    $discoverySource, $sortOrder, $createdAt, $updatedAt
                );
                """;
            Bind(command, toSave);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return toSave;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<HubDevice?> UpdateAsync(Guid id, HubDevice device, CancellationToken cancellationToken)
    {
        var toSave = device with { Id = id };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE Devices SET
                    Name = $name, Kind = $kind, Room = $room, Enabled = $enabled,
                    CommandTopic = $commandTopic, StateTopic = $stateTopic, AvailabilityTopic = $availabilityTopic,
                    PayloadOn = $payloadOn, PayloadOff = $payloadOff,
                    PayloadStop = $payloadStop, Momentary = $momentary,
                    PayloadAvailable = $payloadAvailable, PayloadNotAvailable = $payloadNotAvailable,
                    Schema = $schema, StateValueTemplate = $stateValueTemplate, Optimistic = $optimistic,
                    SupportsBrightness = $supportsBrightness, BrightnessCommandTopic = $brightnessCommandTopic,
                    BrightnessStateTopic = $brightnessStateTopic, BrightnessScale = $brightnessScale,
                    DiscoverySource = $discoverySource, SortOrder = $sortOrder,
                    CreatedAt = $createdAt, UpdatedAt = $updatedAt
                WHERE Id = $id;
                """;
            Bind(command, toSave);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken);
            return rows > 0 ? toSave : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateOrderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var transaction = _connection.BeginTransaction();
            for (var index = 0; index < orderedIds.Count; index++)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Devices SET SortOrder = $sortOrder WHERE Id = $id;";
                command.Parameters.AddWithValue("$sortOrder", index + 1);
                command.Parameters.AddWithValue("$id", orderedIds[index].ToString("N"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Devices WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Bind(SqliteCommand command, HubDevice device)
    {
        command.Parameters.AddWithValue("$id", device.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", device.Name);
        command.Parameters.AddWithValue("$kind", device.Kind);
        command.Parameters.AddWithValue("$room", device.Room);
        command.Parameters.AddWithValue("$enabled", device.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$commandTopic", device.CommandTopic);
        command.Parameters.AddWithValue("$stateTopic", device.StateTopic);
        command.Parameters.AddWithValue("$availabilityTopic", device.AvailabilityTopic);
        command.Parameters.AddWithValue("$payloadOn", device.PayloadOn);
        command.Parameters.AddWithValue("$payloadOff", device.PayloadOff);
        command.Parameters.AddWithValue("$payloadStop", device.PayloadStop);
        command.Parameters.AddWithValue("$momentary", device.Momentary ? 1 : 0);
        command.Parameters.AddWithValue("$payloadAvailable", device.PayloadAvailable);
        command.Parameters.AddWithValue("$payloadNotAvailable", device.PayloadNotAvailable);
        command.Parameters.AddWithValue("$schema", device.Schema);
        command.Parameters.AddWithValue("$stateValueTemplate", device.StateValueTemplate);
        command.Parameters.AddWithValue("$optimistic", device.Optimistic ? 1 : 0);
        command.Parameters.AddWithValue("$supportsBrightness", device.SupportsBrightness ? 1 : 0);
        command.Parameters.AddWithValue("$brightnessCommandTopic", device.BrightnessCommandTopic);
        command.Parameters.AddWithValue("$brightnessStateTopic", device.BrightnessStateTopic);
        command.Parameters.AddWithValue("$brightnessScale", device.BrightnessScale);
        command.Parameters.AddWithValue("$discoverySource", device.DiscoverySource);
        command.Parameters.AddWithValue("$sortOrder", device.SortOrder);
        command.Parameters.AddWithValue("$createdAt", device.CreatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", device.UpdatedAt.UtcDateTime.ToString("O"));
    }

    private static HubDevice ReadDevice(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetString(reader.GetOrdinal("Kind")),
        reader.GetString(reader.GetOrdinal("Room")),
        reader.GetInt64(reader.GetOrdinal("Enabled")) != 0,
        reader.GetString(reader.GetOrdinal("CommandTopic")),
        reader.GetString(reader.GetOrdinal("StateTopic")),
        reader.GetString(reader.GetOrdinal("AvailabilityTopic")),
        reader.GetString(reader.GetOrdinal("PayloadOn")),
        reader.GetString(reader.GetOrdinal("PayloadOff")),
        reader.GetString(reader.GetOrdinal("PayloadStop")),
        reader.GetInt64(reader.GetOrdinal("Momentary")) != 0,
        reader.GetString(reader.GetOrdinal("PayloadAvailable")),
        reader.GetString(reader.GetOrdinal("PayloadNotAvailable")),
        reader.GetString(reader.GetOrdinal("Schema")),
        reader.GetString(reader.GetOrdinal("StateValueTemplate")),
        reader.GetInt64(reader.GetOrdinal("Optimistic")) != 0,
        reader.GetInt64(reader.GetOrdinal("SupportsBrightness")) != 0,
        reader.GetString(reader.GetOrdinal("BrightnessCommandTopic")),
        reader.GetString(reader.GetOrdinal("BrightnessStateTopic")),
        (int)reader.GetInt64(reader.GetOrdinal("BrightnessScale")),
        reader.GetString(reader.GetOrdinal("DiscoverySource")),
        (int)reader.GetInt64(reader.GetOrdinal("SortOrder")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
