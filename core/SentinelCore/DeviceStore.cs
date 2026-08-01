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
    // "light", "switch", "outlet", "fan", "cover", "lock", or "sensor". Everything but "sensor"
    // is commandable; a sensor is display-only and needs no CommandTopic.
    string Kind,
    string Room,
    bool Enabled,
    string CommandTopic,
    string StateTopic,
    string AvailabilityTopic,
    string PayloadOn,
    string PayloadOff,
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
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<HubDevice>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM Devices ORDER BY Room COLLATE NOCASE, Name COLLATE NOCASE;", cancellationToken);

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
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Devices (
                    Id, Name, Kind, Room, Enabled, CommandTopic, StateTopic, AvailabilityTopic,
                    PayloadOn, PayloadOff, PayloadAvailable, PayloadNotAvailable, Schema, StateValueTemplate,
                    Optimistic, SupportsBrightness, BrightnessCommandTopic, BrightnessStateTopic, BrightnessScale,
                    DiscoverySource, CreatedAt, UpdatedAt
                ) VALUES (
                    $id, $name, $kind, $room, $enabled, $commandTopic, $stateTopic, $availabilityTopic,
                    $payloadOn, $payloadOff, $payloadAvailable, $payloadNotAvailable, $schema, $stateValueTemplate,
                    $optimistic, $supportsBrightness, $brightnessCommandTopic, $brightnessStateTopic, $brightnessScale,
                    $discoverySource, $createdAt, $updatedAt
                );
                """;
            Bind(command, device);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return device;
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
                    PayloadAvailable = $payloadAvailable, PayloadNotAvailable = $payloadNotAvailable,
                    Schema = $schema, StateValueTemplate = $stateValueTemplate, Optimistic = $optimistic,
                    SupportsBrightness = $supportsBrightness, BrightnessCommandTopic = $brightnessCommandTopic,
                    BrightnessStateTopic = $brightnessStateTopic, BrightnessScale = $brightnessScale,
                    DiscoverySource = $discoverySource, CreatedAt = $createdAt, UpdatedAt = $updatedAt
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
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
