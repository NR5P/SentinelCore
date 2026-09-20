using System.Text.Json;

namespace LicenseGenerator.Gui;

// Remembers the handful of answers that are identical on every sale - which private.pem, which
// folder the .lic files go in - so issuing a licence is three fields and a button rather than a
// file-picker safari every time. Nothing secret is stored: the key's path, never its contents.
public sealed class AppState
{
    public string? SigningKeyPath { get; set; }
    public string? OutputDirectory { get; set; }
    public string? Product { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string StatePath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(config, "SentinelCore", "license-generator-gui.json");
        }
    }

    public static AppState Load()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState()
                : new AppState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Remembered convenience, nothing more - a corrupt state file just means picking the
            // key again, never a tool that won't start.
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
