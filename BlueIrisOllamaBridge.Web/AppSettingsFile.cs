using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlueIrisOllamaBridge.Web;

// Reads and writes the main app's appsettings.json directly. Section merges only touch the
// keys the caller supplies, so fields this UI doesn't expose (QueueCapacity, DataDirectory,
// BaseUrl, *TimeoutSeconds, Urls, Logging, ...) are always left exactly as they were.
public sealed class AppSettingsFile(string path)
{
    // No naming policy: keeps property names as-is (PascalCase, matching the file's existing
    // convention) rather than converting to camelCase on write. Case-insensitive on read as a
    // safety net.
    private static readonly JsonSerializerOptions SectionOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<T?> GetSectionAsync<T>(string sectionName, CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(cancellationToken);
        return root[sectionName].Deserialize<T>(SectionOptions);
    }

    // Overlays only the properties present in `updates` onto the existing section,
    // preserving any other keys already in that section untouched.
    public async Task MergeSectionAsync<T>(string sectionName, T updates, CancellationToken cancellationToken)
    {
        var updatesNode = JsonSerializer.SerializeToNode(updates, SectionOptions) as JsonObject
            ?? throw new InvalidOperationException($"{typeof(T).Name} did not serialize to a JSON object.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootUnlockedAsync(cancellationToken);

            if (root[sectionName] is not JsonObject existingSection)
            {
                existingSection = new JsonObject();
                root[sectionName] = existingSection;
            }

            foreach (var (key, value) in updatesNode)
                existingSection[key] = value?.DeepClone();

            await WriteRootUnlockedAsync(root, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Full replace, for sections like PromptProfiles where every key is user-owned data
    // (there's nothing "hidden" to preserve, unlike Bridge/Ollama/etc).
    public async Task ReplaceSectionAsync<T>(string sectionName, T value, CancellationToken cancellationToken)
    {
        var valueNode = JsonSerializer.SerializeToNode(value, SectionOptions);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootUnlockedAsync(cancellationToken);
            root[sectionName] = valueNode;
            await WriteRootUnlockedAsync(root, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await ReadRootUnlockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JsonObject> ReadRootUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return node as JsonObject ?? throw new InvalidOperationException($"{path} does not contain a JSON object at its root.");
    }

    private async Task WriteRootUnlockedAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, root, WriteOptions, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
