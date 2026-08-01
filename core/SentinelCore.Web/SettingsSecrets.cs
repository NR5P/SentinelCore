using System.Reflection;
using System.Text.Json.Nodes;

namespace SentinelCore.Web;

// Keeps stored secrets (SMTP passwords, bot tokens, API keys) out of settings reads. Now that
// phones and TVs render the same settings forms the dashboard does, GET /api/settings travels a
// lot further than one browser tab on the LAN - so secrets go out replaced by a fixed mask, and
// a save that sends the mask back untouched means "keep what's stored". Clients never need the
// real value: the only thing a settings form does with a stored secret is show that it's set.
//
// Which fields count as secret comes from SettingsSchema (every "password" field), so a new
// secret added there is masked here automatically - no second list to forget to update.
public static class SettingsSecrets
{
    public const string Mask = "••••••••";

    private static readonly Dictionary<Type, string[]> SecretPathsByDtoType = SettingsSchema.Sections
        .Select(s => (s.DtoType, Paths: s.Fields.Where(f => f.Secret).Select(f => f.Name).ToArray()))
        .Where(x => x.Paths.Length > 0)
        .ToDictionary(x => x.DtoType, x => x.Paths);

    // Replaces every non-empty secret in the snapshot with the mask. Empty stays empty - the
    // form has to be able to show the difference between "set" and "never configured".
    public static void MaskInPlace(SettingsSnapshot snapshot)
    {
        foreach (var property in typeof(SettingsSnapshot).GetProperties())
        {
            if (!SecretPathsByDtoType.TryGetValue(property.PropertyType, out var paths)) continue;
            if (property.GetValue(snapshot) is not object dto) continue;

            foreach (var path in paths)
            {
                if (ResolvePath(dto, path) is not var (owner, leaf)) continue;
                if (leaf.GetValue(owner) is string { Length: > 0 })
                    leaf.SetValue(owner, Mask);
            }
        }
    }

    // The inverse, applied to an incoming save: any secret field carrying the literal mask is
    // swapped back to the stored value before validation runs - validation, because rules like
    // Bridge.ApiKey's MinLength must judge the real value, not the eight mask characters.
    public static async Task UnmaskAsync(object dto, string sectionName, AppSettingsFile settings, CancellationToken cancellationToken)
    {
        if (!SecretPathsByDtoType.TryGetValue(dto.GetType(), out var paths)) return;

        JsonObject? stored = null;
        var storedLoaded = false;

        foreach (var path in paths)
        {
            if (ResolvePath(dto, path) is not var (owner, leaf)) continue;
            if (leaf.GetValue(owner) is not Mask) continue;

            if (!storedLoaded)
            {
                stored = await settings.GetSectionAsync<JsonObject>(sectionName, cancellationToken);
                storedLoaded = true;
            }

            leaf.SetValue(owner, ReadStoredString(stored, path) ?? string.Empty);
        }
    }

    // Walks a dotted camelCase path ("apple.privateKeyPath") down a DTO instance to the property
    // holding the leaf value. Case-insensitive per segment, since the paths are camelCase and
    // the C# properties are PascalCase.
    private static (object Owner, PropertyInfo Leaf)? ResolvePath(object root, string path)
    {
        var segments = path.Split('.');
        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var step = current.GetType().GetProperty(
                segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (step?.GetValue(current) is not object next) return null;
            current = next;
        }

        var leaf = current.GetType().GetProperty(
            segments[^1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return leaf is null ? null : (current, leaf);
    }

    // Same walk over the stored JSON, which uses PascalCase keys on disk (matching the file's
    // convention) - hence the case-insensitive key match rather than a plain indexer.
    private static string? ReadStoredString(JsonObject? section, string path)
    {
        JsonNode? node = section;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject obj) return null;
            var key = obj.Select(kv => kv.Key)
                .FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase));
            if (key is null) return null;
            node = obj[key];
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
