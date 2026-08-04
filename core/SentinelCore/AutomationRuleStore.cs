using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace SentinelCore;

// A user-defined "when this camera sees that, do this" rule - the generalized version of the
// fixed-topic MqttAlertAction/single-URL WebhookAlertAction above. Cameras/Categories empty means
// "any"; ActionType picks which of the Mqtt*/Webhook* fields are actually used. See
// AutomationAlertAction for how a rule is matched and dispatched, and AutomationTemplate for the
// {{token}} substitution available in MqttTopic/MqttPayload/WebhookUrl/WebhookHeaders/WebhookBody.
public sealed record AutomationRule(
    Guid Id,
    string Name,
    bool Enabled,
    IReadOnlyList<string> Cameras,
    IReadOnlyList<string> Categories,
    double MinimumConfidence,
    bool OnlyWhenNotificationRecommended,
    int DuplicateCooldownMinutes,
    string ActionType,
    string MqttTopic,
    string MqttPayload,
    bool MqttRetain,
    string WebhookUrl,
    string WebhookMethod,
    string WebhookHeaders,
    string WebhookBody,
    string WebhookContentType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    // The rule's action half, in the shape AutomationActionDispatcher takes - which is also what
    // a Schedule stores directly. Rules do not offer the dispatcher's "device" type yet; their
    // table predates it, and every existing row is an MQTT publish or a webhook call.
    public AutomationAction ToAction() => new(
        ActionType, DeviceId: null, DeviceAction: "", DeviceBrightness: null,
        MqttTopic, MqttPayload, MqttRetain,
        WebhookUrl, WebhookMethod, WebhookHeaders, WebhookBody, WebhookContentType);
}

public interface IAutomationRuleStore
{
    Task<IReadOnlyList<AutomationRule>> GetAllAsync(CancellationToken cancellationToken);

    // Hot path for AutomationAlertAction - only rows with Enabled = 1, so a disabled rule costs
    // nothing per alert beyond the query itself.
    Task<IReadOnlyList<AutomationRule>> GetEnabledAsync(CancellationToken cancellationToken);
    Task<AutomationRule?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AutomationRule> CreateAsync(AutomationRule rule, CancellationToken cancellationToken);
    Task<AutomationRule?> UpdateAsync(Guid id, AutomationRule rule, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class SqliteAutomationRuleStore : IAutomationRuleStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqliteAutomationRuleStore(IOptions<BridgeOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.DataDirectory;
        var root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "automation-rules.db");

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AutomationRules (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Cameras TEXT NOT NULL,
                Categories TEXT NOT NULL,
                MinimumConfidence REAL NOT NULL,
                OnlyWhenNotificationRecommended INTEGER NOT NULL,
                DuplicateCooldownMinutes INTEGER NOT NULL,
                ActionType TEXT NOT NULL,
                MqttTopic TEXT NOT NULL,
                MqttPayload TEXT NOT NULL,
                MqttRetain INTEGER NOT NULL,
                WebhookUrl TEXT NOT NULL,
                WebhookMethod TEXT NOT NULL,
                WebhookHeaders TEXT NOT NULL,
                WebhookBody TEXT NOT NULL,
                WebhookContentType TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<AutomationRule>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM AutomationRules ORDER BY Name COLLATE NOCASE;", cancellationToken);

    public Task<IReadOnlyList<AutomationRule>> GetEnabledAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM AutomationRules WHERE Enabled = 1;", cancellationToken);

    private async Task<IReadOnlyList<AutomationRule>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;

            var results = new List<AutomationRule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadRule(reader));

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AutomationRule?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT * FROM AutomationRules WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadRule(reader) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AutomationRule> CreateAsync(AutomationRule rule, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AutomationRules (
                    Id, Name, Enabled, Cameras, Categories, MinimumConfidence, OnlyWhenNotificationRecommended,
                    DuplicateCooldownMinutes, ActionType, MqttTopic, MqttPayload, MqttRetain,
                    WebhookUrl, WebhookMethod, WebhookHeaders, WebhookBody, WebhookContentType, CreatedAt, UpdatedAt
                ) VALUES (
                    $id, $name, $enabled, $cameras, $categories, $minConfidence, $onlyRecommended,
                    $cooldown, $actionType, $mqttTopic, $mqttPayload, $mqttRetain,
                    $webhookUrl, $webhookMethod, $webhookHeaders, $webhookBody, $webhookContentType, $createdAt, $updatedAt
                );
                """;
            Bind(command, rule);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return rule;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AutomationRule?> UpdateAsync(Guid id, AutomationRule rule, CancellationToken cancellationToken)
    {
        var toSave = rule with { Id = id };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE AutomationRules SET
                    Name = $name, Enabled = $enabled, Cameras = $cameras, Categories = $categories,
                    MinimumConfidence = $minConfidence, OnlyWhenNotificationRecommended = $onlyRecommended,
                    DuplicateCooldownMinutes = $cooldown, ActionType = $actionType,
                    MqttTopic = $mqttTopic, MqttPayload = $mqttPayload, MqttRetain = $mqttRetain,
                    WebhookUrl = $webhookUrl, WebhookMethod = $webhookMethod, WebhookHeaders = $webhookHeaders,
                    WebhookBody = $webhookBody, WebhookContentType = $webhookContentType,
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

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM AutomationRules WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("N"));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void Bind(SqliteCommand command, AutomationRule rule)
    {
        command.Parameters.AddWithValue("$id", rule.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", rule.Name);
        command.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$cameras", JsonSerializer.Serialize(rule.Cameras));
        command.Parameters.AddWithValue("$categories", JsonSerializer.Serialize(rule.Categories));
        command.Parameters.AddWithValue("$minConfidence", rule.MinimumConfidence);
        command.Parameters.AddWithValue("$onlyRecommended", rule.OnlyWhenNotificationRecommended ? 1 : 0);
        command.Parameters.AddWithValue("$cooldown", rule.DuplicateCooldownMinutes);
        command.Parameters.AddWithValue("$actionType", rule.ActionType);
        command.Parameters.AddWithValue("$mqttTopic", rule.MqttTopic);
        command.Parameters.AddWithValue("$mqttPayload", rule.MqttPayload);
        command.Parameters.AddWithValue("$mqttRetain", rule.MqttRetain ? 1 : 0);
        command.Parameters.AddWithValue("$webhookUrl", rule.WebhookUrl);
        command.Parameters.AddWithValue("$webhookMethod", rule.WebhookMethod);
        command.Parameters.AddWithValue("$webhookHeaders", rule.WebhookHeaders);
        command.Parameters.AddWithValue("$webhookBody", rule.WebhookBody);
        command.Parameters.AddWithValue("$webhookContentType", rule.WebhookContentType);
        command.Parameters.AddWithValue("$createdAt", rule.CreatedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", rule.UpdatedAt.UtcDateTime.ToString("O"));
    }

    private static AutomationRule ReadRule(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(reader.GetOrdinal("Id")), "N"),
        reader.GetString(reader.GetOrdinal("Name")),
        reader.GetInt64(reader.GetOrdinal("Enabled")) != 0,
        JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Cameras"))) ?? [],
        JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Categories"))) ?? [],
        reader.GetDouble(reader.GetOrdinal("MinimumConfidence")),
        reader.GetInt64(reader.GetOrdinal("OnlyWhenNotificationRecommended")) != 0,
        (int)reader.GetInt64(reader.GetOrdinal("DuplicateCooldownMinutes")),
        reader.GetString(reader.GetOrdinal("ActionType")),
        reader.GetString(reader.GetOrdinal("MqttTopic")),
        reader.GetString(reader.GetOrdinal("MqttPayload")),
        reader.GetInt64(reader.GetOrdinal("MqttRetain")) != 0,
        reader.GetString(reader.GetOrdinal("WebhookUrl")),
        reader.GetString(reader.GetOrdinal("WebhookMethod")),
        reader.GetString(reader.GetOrdinal("WebhookHeaders")),
        reader.GetString(reader.GetOrdinal("WebhookBody")),
        reader.GetString(reader.GetOrdinal("WebhookContentType")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt"))));

    public void Dispose()
    {
        _lock.Dispose();
        _connection.Dispose();
    }
}
