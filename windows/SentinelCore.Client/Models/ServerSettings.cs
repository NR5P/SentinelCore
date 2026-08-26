using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentinelCore.Client.Models;

// The server-side settings schema - which sections exist, what fields they contain, and how to
// render each one. Served by GET /api/settings/schema and shared verbatim with the web dashboard,
// the Android app and the Apple apps, so a settings section added on the server appears here with
// no app update. This file only mirrors the shapes; ServerSettingsView renders them.

public sealed class SettingsFieldOption
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
}

// An extra button a section offers (currently just Bridge's "re-run setup wizard") - pure data, an
// endpoint to POST to, so no client needs bespoke code per action.
public sealed class SettingsActionSchema
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "POST";
}

// What an action reports back. Only the test sends fill this in ("Test email sent to ..."), and
// only they need to: an action whose whole result is "it worked" says it with its status code.
public sealed class SettingsActionResult
{
    public string? Detail { get; init; }
}

// A "look this up for me" button on a text field (today: LOCATION's Town or City, which fills in
// latitude and longitude). Pure data - an endpoint taking ?query= - so nothing here is
// location-specific, and a field without one is just a text box.
public sealed class SettingsLookupSchema
{
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    public string Endpoint { get; init; } = "";
}

// One option a lookup came back with: something to show in the list, and the field values to write
// if it is picked. The keys are field names in the same section; a client applies the ones it
// recognises and ignores the rest, so a newer server can't break an older app.
public sealed class SettingsLookupCandidate
{
    public string Label { get; init; } = "";
    public Dictionary<string, SettingsValue> Values { get; init; } = new();
}

public sealed class SettingsLookupResponse
{
    public List<SettingsLookupCandidate> Results { get; init; } = new();
}

// Where a text field's likely values can be fetched from (today: the models pulled on the Ollama
// machine). Pure data like SettingsLookupSchema - an endpoint taking no parameters.
//
// A field carrying one is drawn as a picker, but never as a plain picker: the value that is saved
// now is always in the list, and "Type a name instead..." is always the last entry. The list is
// only what is installed at this moment, and naming a model you are about to pull has to keep
// working.
public sealed class SettingsOptionsSource
{
    public string Label { get; init; } = "";
    public string Tip { get; init; } = "";
    public string Endpoint { get; init; } = "";

    // Shown when the endpoint answers with an empty list. From the server because it is the one
    // message that names what to go and do ("ollama pull ..."), which this app shouldn't know.
    public string EmptyMessage { get; init; } = "";

    // Shown after picking "type a name instead", from the server for the same reason.
    public string TypeItMessage { get; init; } = "";
}

public sealed class SettingsOptionsResponse
{
    public List<SettingsFieldOption> Options { get; init; } = new();
}

public sealed class SettingsFieldSchema
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";

    // "text", "number", "checkbox", "password", "select", or "csv". Anything unrecognized is
    // rendered read-only rather than guessed at - better than writing back something malformed.
    public string Type { get; init; } = "text";

    public string Tip { get; init; } = "";
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }

    // Whether a "number" field holds a whole number, so 3.7 in a port box is caught here with a
    // sentence rather than by the server as a model-binding failure. Optional, unlike Secret and
    // RequiresRestart next to it, only because it was added later: against a server that predates
    // it the value is null, and null has to mean "don't apply a restriction this server never
    // asked for".
    public bool? Integer { get; init; }

    // What to use if you have no opinion, for the numeric settings where that is the honest
    // position. Absent on most fields, and deliberately not the same thing as the shipped default.
    public double? Recommended { get; init; }

    public List<SettingsFieldOption>? Options { get; init; }

    // Absent on all but a handful of text fields - see SettingsLookupSchema.
    public SettingsLookupSchema? Lookup { get; init; }

    // Absent on all but a handful of text fields - see SettingsOptionsSource.
    public SettingsOptionsSource? OptionsSource { get; init; }

    public bool RequiresRestart { get; init; }
    public bool Secret { get; init; }
}

public sealed class SettingsSectionSchema
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public bool Basic { get; init; }
    public string Description { get; init; } = "";
    public List<SettingsFieldSchema> Fields { get; init; } = new();
    public SettingsActionSchema? ExtraAction { get; init; }

    // Which heading this section belongs under, and where that heading sorts. Both come from the
    // server (SettingsGroups) so this app and the web dashboard divide the settings up the same
    // way without either holding its own copy of the grouping.
    //
    // Optional purely so this app still renders against an older server that predates grouping -
    // there, everything lands in one unnamed group, which is exactly the flat list it had before.
    public string? Group { get; init; }
    public int? GroupOrder { get; init; }

    // Which service has to restart for RequiresRestart fields here to take effect. Defaults to the
    // bridge, as every section but INTERNET EXPOSED MODE does.
    public string? RestartService { get; init; }

    // Where to GET this section's current values, when they aren't in the bulk /api/settings
    // snapshot. Only INTERNET EXPOSED MODE sets it.
    public string? DataEndpoint { get; init; }

    [JsonIgnore] public bool NeedsRestart => Fields.Any(field => field.RequiresRestart);

    [JsonIgnore] public string RestartTarget => RestartService ?? "bridge";
}

// One JSON value of any shape. GET /api/settings mixes strings, numbers, booleans, arrays, and
// nested objects (PushNotifications' apple/firebase blocks) in a single payload whose shape the
// app deliberately doesn't hard-code - that is the schema's job.
[JsonConverter(typeof(SettingsValueConverter))]
public sealed class SettingsValue : IEquatable<SettingsValue>
{
    public enum ValueKind
    {
        Null,
        String,
        Number,
        Bool,
        Object,
        Array,
    }

    public ValueKind Kind { get; private init; }
    public string? StringValue { get; private init; }
    public double NumberValue { get; private init; }
    public bool BoolValue { get; private init; }
    public Dictionary<string, SettingsValue>? ObjectValue { get; private init; }
    public List<SettingsValue>? ArrayValue { get; private init; }

    public static SettingsValue Null { get; } = new() { Kind = ValueKind.Null };

    public static SettingsValue FromString(string value) =>
        new() { Kind = ValueKind.String, StringValue = value };

    public static SettingsValue FromNumber(double value) =>
        new() { Kind = ValueKind.Number, NumberValue = value };

    public static SettingsValue FromBool(bool value) =>
        new() { Kind = ValueKind.Bool, BoolValue = value };

    public static SettingsValue FromObject(Dictionary<string, SettingsValue> value) =>
        new() { Kind = ValueKind.Object, ObjectValue = value };

    public static SettingsValue FromArray(List<SettingsValue> value) =>
        new() { Kind = ValueKind.Array, ArrayValue = value };

    // Field names in the schema can be dotted paths into nested objects ("apple.teamId") - the
    // same convention the dashboard's readPath/writePath use.
    public static SettingsValue? Read(IReadOnlyDictionary<string, SettingsValue>? source, string path)
    {
        if (source is null) return null;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        IReadOnlyDictionary<string, SettingsValue> current = source;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetValue(segments[i], out var nested) ||
                nested.Kind != ValueKind.Object ||
                nested.ObjectValue is null)
            {
                return null;
            }
            current = nested.ObjectValue;
        }

        return current.TryGetValue(segments[^1], out var leaf) ? leaf : null;
    }

    public static void Write(Dictionary<string, SettingsValue> target, string path, SettingsValue value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;
        if (segments.Length == 1)
        {
            target[segments[0]] = value;
            return;
        }

        // Rebuild the nested object chain - paths here are at most two levels deep
        // ("apple.teamId"), but this handles any depth the schema might grow.
        var head = segments[0];
        var nested = target.TryGetValue(head, out var existing) &&
                     existing.Kind == ValueKind.Object &&
                     existing.ObjectValue is not null
            ? new Dictionary<string, SettingsValue>(existing.ObjectValue)
            : new Dictionary<string, SettingsValue>();

        Write(nested, string.Join('.', segments.Skip(1)), value);
        target[head] = FromObject(nested);
    }

    // Display forms for seeding text fields. Integral numbers drop the ".0" so a port renders as
    // 1883, not 1883.0.
    public string DisplayString => Kind switch
    {
        ValueKind.String => StringValue ?? "",
        ValueKind.Bool => BoolValue ? "true" : "false",
        ValueKind.Number => NumberValue == Math.Round(NumberValue) && Math.Abs(NumberValue) < 1e15
            ? ((long)NumberValue).ToString(CultureInfo.InvariantCulture)
            : NumberValue.ToString(CultureInfo.InvariantCulture),
        ValueKind.Array => string.Join(", ", (ArrayValue ?? new List<SettingsValue>()).Select(v => v.DisplayString)),
        _ => "",
    };

    public bool AsBool => Kind == ValueKind.Bool && BoolValue;

    public bool Equals(SettingsValue? other)
    {
        if (other is null) return false;
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ValueKind.Null => true,
            ValueKind.String => StringValue == other.StringValue,
            ValueKind.Number => NumberValue.Equals(other.NumberValue),
            ValueKind.Bool => BoolValue == other.BoolValue,
            ValueKind.Array => (ArrayValue ?? new()).SequenceEqual(other.ArrayValue ?? new()),
            ValueKind.Object => (ObjectValue ?? new()).Count == (other.ObjectValue ?? new()).Count &&
                                (ObjectValue ?? new()).All(pair =>
                                    (other.ObjectValue ?? new()).TryGetValue(pair.Key, out var value) &&
                                    pair.Value.Equals(value)),
            _ => false,
        };
    }

    public override bool Equals(object? obj) => Equals(obj as SettingsValue);

    public override int GetHashCode() => Kind switch
    {
        ValueKind.String => StringValue?.GetHashCode() ?? 0,
        ValueKind.Number => NumberValue.GetHashCode(),
        ValueKind.Bool => BoolValue.GetHashCode(),
        _ => (int)Kind,
    };

    public override string ToString() => DisplayString;
}

public sealed class SettingsValueConverter : JsonConverter<SettingsValue>
{
    // Without this, System.Text.Json handles a JSON null itself and hands back a null reference
    // rather than calling this converter - so a settings snapshot containing "password": null (which
    // every unconfigured secret is) would put a null into the dictionary, and the settings screen
    // would throw the moment it read that field rather than drawing an empty box.
    public override bool HandleNull => true;

    public override SettingsValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return SettingsValue.Null;
            case JsonTokenType.True:
                return SettingsValue.FromBool(true);
            case JsonTokenType.False:
                return SettingsValue.FromBool(false);
            case JsonTokenType.Number:
                return SettingsValue.FromNumber(reader.GetDouble());
            case JsonTokenType.String:
                return SettingsValue.FromString(reader.GetString() ?? "");
            case JsonTokenType.StartArray:
            {
                var items = new List<SettingsValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(Read(ref reader, typeToConvert, options));
                }
                return SettingsValue.FromArray(items);
            }
            case JsonTokenType.StartObject:
            {
                var fields = new Dictionary<string, SettingsValue>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString() ?? "";
                    reader.Read();
                    fields[name] = Read(ref reader, typeToConvert, options);
                }
                return SettingsValue.FromObject(fields);
            }
            default:
                throw new JsonException($"Unsupported JSON value: {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, SettingsValue value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case SettingsValue.ValueKind.Null:
                writer.WriteNullValue();
                break;
            case SettingsValue.ValueKind.String:
                writer.WriteStringValue(value.StringValue);
                break;
            case SettingsValue.ValueKind.Number:
                writer.WriteNumberValue(value.NumberValue);
                break;
            case SettingsValue.ValueKind.Bool:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case SettingsValue.ValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.ArrayValue ?? new List<SettingsValue>())
                {
                    Write(writer, item, options);
                }
                writer.WriteEndArray();
                break;
            case SettingsValue.ValueKind.Object:
                writer.WriteStartObject();
                foreach (var pair in value.ObjectValue ?? new Dictionary<string, SettingsValue>())
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value, options);
                }
                writer.WriteEndObject();
                break;
        }
    }
}

public sealed class SettingsSaveResult
{
    public bool? Saved { get; init; }
    public bool? RestartRequired { get; init; }
}

/// <summary>
/// Getting one section's values out of the bulk settings snapshot.
/// </summary>
/// <remarks>
/// GET /api/settings answers with an object keyed by section - {"mqtt": {...}, "email": {...}} -
/// while a schema field's name is relative to its own section ("host", or "apple.teamId" for a
/// nested one). Reading a field name straight off the top of the snapshot therefore finds nothing
/// at all, and every box on the settings screen renders empty against a server that is answering
/// perfectly. The web dashboard does the same unwrapping (renderSection(schema, data[schema.key])
/// in wwwroot/settings.js); this is that step, in one place, with tests.
///
/// The save side needs no equivalent: a section is PUT to its own endpoint with field names at the
/// top level, which is what the dashboard sends too.
/// </remarks>
public static class SettingsSnapshot
{
    /// <summary>
    /// The values belonging to one section, or an empty set when the snapshot has no such key.
    /// </summary>
    /// <remarks>
    /// An absent key is not an error: a section the server knows about but has never had values
    /// written for is exactly a form full of defaults, which is what an empty set renders.
    /// </remarks>
    public static Dictionary<string, SettingsValue> SectionValues(
        IReadOnlyDictionary<string, SettingsValue>? snapshot,
        string sectionKey)
    {
        if (snapshot is null || string.IsNullOrEmpty(sectionKey)) return new Dictionary<string, SettingsValue>();

        return snapshot.TryGetValue(sectionKey, out var section) &&
               section.Kind == SettingsValue.ValueKind.Object &&
               section.ObjectValue is { } fields
            ? new Dictionary<string, SettingsValue>(fields)
            : new Dictionary<string, SettingsValue>();
    }
}

// Rebooting the machine the server runs on, as opposed to restarting one of its services -
// mirrors HostRebootProbe in HostReboot.cs.
//
// Deliberately carries the server's prose rather than reconstructing it here. Which sudoers line
// to add, on which path, for which user, is a question only the server can answer about itself,
// and an app that guessed would eventually guess wrong on someone's install.
public sealed class HostRebootProbe
{
    public string Platform { get; init; } = "";
    public string PlatformLabel { get; init; } = "";
    public string Readiness { get; init; } = "";
    public bool Enabled { get; init; }
    public string RunningAs { get; init; } = "";
    public string Summary { get; init; } = "";
    public int DelaySeconds { get; init; }
    public string? SetupScript { get; init; }
    public string? SetupNote { get; init; }
    public DateTimeOffset? ScheduledFor { get; init; }

    // Counted down from locally. Preferred over subtracting ScheduledFor from this machine's
    // clock, which on a PC that has drifted would produce a confidently wrong countdown.
    public int? SecondsRemaining { get; init; }

    [JsonIgnore] public bool IsReady => Readiness == "ready";

    [JsonIgnore] public bool NeedsSetup => Readiness == "setupRequired";

    [JsonIgnore] public bool IsUnsupported => Readiness == "unsupported";

    // Ready and switched on are separate facts, and the difference is the whole reason this screen
    // exists: one is fixed in this app, the other only at a shell on the server.
    [JsonIgnore] public bool CanReboot => IsReady && Enabled;
}

public sealed class HostRebootOutcome
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset? ScheduledFor { get; init; }
    public int? SecondsRemaining { get; init; }
}
