using System.Text.Json;

namespace SentinelCore.Client;

/// <summary>
/// The handful of things this app remembers between runs: which server to talk to, and the window
/// it was last sized to. The Apple client uses UserDefaults; this is the same idea as a small JSON
/// file under %LOCALAPPDATA%.
/// </summary>
/// <remarks>
/// Deliberately not the registry. A file can be copied to another machine, inspected when a
/// connection misbehaves, and deleted to get back to a fresh install - none of which is true of a
/// registry key someone has to be talked through finding.
/// </remarks>
public sealed class PreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, JsonElement> _values;

    public PreferenceStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SentinelCore",
            "settings.json");
        _values = Read(_path);
    }

    public string GetString(string key, string fallback = "") =>
        TryGet(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        TryGet(key, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        TryGet(key, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : fallback;

    public void Set(string key, string value) => Store(key, JsonSerializer.SerializeToElement(value));

    public void Set(string key, bool value) => Store(key, JsonSerializer.SerializeToElement(value));

    public void Set(string key, double value) => Store(key, JsonSerializer.SerializeToElement(value));

    private bool TryGet(string key, out JsonElement element)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out element);
        }
    }

    private void Store(string key, JsonElement value)
    {
        lock (_gate)
        {
            _values[key] = value;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (IOException)
            {
                // A preference that couldn't be written is a preference that won't survive a
                // restart - annoying, but never a reason to take the app down mid-session.
            }
        }
    }

    private static Dictionary<string, JsonElement> Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, JsonElement>();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return new Dictionary<string, JsonElement>();
        }
    }
}
