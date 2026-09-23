using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SentinelCore.Client.Organizer;

// The Organizer's data as this app holds it - notes, events, reminders, boards and everything
// hanging off them. The wire protocol is core/docs/ORGANIZER_SYNC.md; the Apple apps' copy of this
// is ios/OrganizerKit and Android's is android/.../organizer, and all three should read alike.
//
// Fields are kept as plain values (string, double, bool, List<object?>, Dictionary<string, object?>,
// null) rather than decoded into fixed classes. A field this build doesn't know about - one a newer
// app added - still has to be stored and sent back untouched, or an older PC would quietly erase it.

public static class OrganizerKinds
{
    public const string Note = "note";
    public const string Event = "event";
    public const string Reminder = "reminder";
    public const string Board = "board";
    public const string Column = "column";
    public const string Card = "card";
    public const string Comment = "comment";
    public const string Attachment = "attachment";
}

/// <summary>
/// Comments on a board card: items of kind <c>comment</c> under the card, with <c>body</c> and
/// <c>editedAt</c>. The author is whoever created it, and only they may change it; anyone may
/// delete one. The server enforces that; these keep the UI from offering what it would refuse.
/// </summary>
public static class OrganizerComments
{
    /// <summary>A card's comments, oldest first.</summary>
    public static List<OrganizerItem> Of(OrganizerEngine engine, string cardId) =>
        Sorted(engine.Children(cardId, OrganizerKinds.Comment));

    public static List<OrganizerItem> Sorted(IEnumerable<OrganizerItem> comments) =>
        comments.OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MaxValue).ThenBy(comment => comment.Id, StringComparer.Ordinal).ToList();

    /// <summary>Whether <paramref name="username"/> (the signed-in account, null with login off)
    /// may edit this comment. A comment with no recorded author, or any comment when there are no
    /// accounts, is everyone's.</summary>
    public static bool CanEdit(OrganizerItem comment, string? username) =>
        string.IsNullOrEmpty(username)
        || string.IsNullOrEmpty(comment.CreatedBy)
        || string.Equals(comment.CreatedBy, username, StringComparison.OrdinalIgnoreCase);

    /// <summary>Who wrote it: a display name when one is known (keyed by lower-case username), the
    /// username otherwise, and null when it wasn't recorded (login off, one shared household) -
    /// then no name is shown at all.</summary>
    public static string? Author(OrganizerItem comment, IReadOnlyDictionary<string, string>? names = null)
    {
        if (string.IsNullOrEmpty(comment.CreatedBy)) return null;
        return names?.GetValueOrDefault(comment.CreatedBy.ToLowerInvariant()) is { Length: > 0 } name ? name : comment.CreatedBy;
    }

    /// <summary>The two letters on a comment's avatar: first letters of the first two words, or the
    /// first two letters of a one-word name. Null without a name (login off), which shows a person glyph.</summary>
    public static string? Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var words = name.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var letters = words.Length > 1 ? string.Concat(words.Take(2).Select(word => word[0])) : name.Trim()[..Math.Min(2, name.Trim().Length)];
        return letters.ToUpperInvariant();
    }

    /// <summary>Which avatar colour a person gets, out of <paramref name="count"/>. Stable per name,
    /// and the same sum of characters as the Apple and Android apps, so an author keeps one colour
    /// on every device.</summary>
    public static int ColorIndex(string name, int count) => name.ToLowerInvariant().Sum(c => (int)c) % count;

    /// <summary>The change for an author rewriting their comment, or null when there's nothing to save.</summary>
    public static Dictionary<string, object?>? EditChanges(OrganizerItem comment, string body, DateTimeOffset? now = null)
    {
        body = body.Trim();
        if (body.Length == 0 || body == comment.String("body")) return null;
        return new Dictionary<string, object?> { ["body"] = body, ["editedAt"] = OrganizerDates.Timestamp(now) };
    }
}

public sealed record OrganizerItem(
    string Id,
    string Kind,
    string SpaceId,
    string? ParentId = null,
    bool Deleted = false,
    IReadOnlyDictionary<string, object?>? Fields = null,
    IReadOnlyDictionary<string, string>? Clocks = null,
    long? Seq = null,
    DateTimeOffset? CreatedAt = null,
    string? CreatedBy = null,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null)
{
    private static readonly IReadOnlyDictionary<string, object?> NoFields = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, string> NoClocks = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, object?> Fields { get; init; } = Fields ?? NoFields;
    public IReadOnlyDictionary<string, string> Clocks { get; init; } = Clocks ?? NoClocks;

    public string? String(string name) => Fields.TryGetValue(name, out var value) ? value as string : null;

    public bool Bool(string name) => Fields.TryGetValue(name, out var value) && value is true;

    public double? Number(string name) => Fields.TryGetValue(name, out var value) ? value switch
    {
        double d => d,
        int i => i,
        long l => l,
        _ => null,
    } : null;

    public IReadOnlyList<object?> List(string name) =>
        Fields.TryGetValue(name, out var value) && value is IReadOnlyList<object?> list ? list : Array.Empty<object?>();

    public string Title => String("title") ?? "";

    // Items without a position sort after those with one, by when they were made.
    public double SortPosition => Number("position") ?? (CreatedAt?.ToUnixTimeMilliseconds() ?? 0);
}

public enum OrganizerSpaceKind { Family, Personal, Group }

public sealed record OrganizerSpace(
    string Id,
    OrganizerSpaceKind Kind,
    string Name,
    IReadOnlyList<string>? Members = null,
    string? Color = null,
    string? CreatedBy = null)
{
    public IReadOnlyList<string> Members { get; init; } = Members ?? Array.Empty<string>();

    public static readonly OrganizerSpace Family = new("family", OrganizerSpaceKind.Family, "Family");
}

/// <summary>
/// Edits to one item not yet acknowledged by the server. A second edit to the same field replaces
/// the first; the first base clock is kept, since that's where the whole run of edits started.
/// </summary>
public sealed record OrganizerChange(
    string Id,
    string Kind,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyDictionary<string, string> Clocks,
    IReadOnlyDictionary<string, string>? BaseClocks = null)
{
    public OrganizerChange(string id, string kind)
        : this(id, kind, new Dictionary<string, object?>(), new Dictionary<string, string>()) { }

    public OrganizerChange Merging(OrganizerChange newer) => this with
    {
        Fields = Combine(Fields, newer.Fields),
        Clocks = Combine(Clocks, newer.Clocks),
        BaseClocks = (BaseClocks, newer.BaseClocks) switch
        {
            (_, null) => BaseClocks,
            (null, _) => newer.BaseClocks,
            _ => Combine(newer.BaseClocks, BaseClocks),
        },
    };

    // The later dictionary's entries win.
    internal static Dictionary<string, T> Combine<T>(IReadOnlyDictionary<string, T> first, IReadOnlyDictionary<string, T> second)
    {
        var result = new Dictionary<string, T>(first);
        foreach (var (key, value) in second) result[key] = value;
        return result;
    }
}

public sealed record OrganizerStatus(
    bool Enabled,
    string ServerId,
    string? Username,
    string? DisplayName,
    bool PersonalSpaces,
    int? ItemCount,
    long? MaxAttachmentBytes);

public sealed record OrganizerPerson(string Username, string? DisplayName)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
}

/// <summary>JSON in and out, in the exact shapes the server speaks.</summary>
public static class OrganizerJson
{
    public static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => Map(obj),
        JsonArray array => array.Select(ToPlain).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetValue<double>(),
            _ => null,
        },
        _ => null,
    };

    public static Dictionary<string, object?> Map(JsonObject? json)
    {
        var result = new Dictionary<string, object?>();
        if (json is null) return result;
        foreach (var (key, value) in json) result[key] = ToPlain(value);
        return result;
    }

    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        // Whole numbers go out without a fraction so a priority of 2 reads back as 2 everywhere.
        double d when Math.Abs(d) < 9.007199254740992E15 && d == Math.Round(d) => JsonValue.Create((long)d),
        double d => JsonValue.Create(d),
        float f => ToNode((double)f),
        int i => JsonValue.Create((long)i),
        long l => JsonValue.Create(l),
        IReadOnlyDictionary<string, object?> map => ObjectOf(map),
        IDictionary<string, object?> map => ObjectOf(map),
        IReadOnlyDictionary<string, string> strings => ObjectOf(strings.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)),
        System.Collections.IEnumerable list => new JsonArray(list.Cast<object?>().Select(ToNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject ObjectOf(IEnumerable<KeyValuePair<string, object?>> map)
    {
        var json = new JsonObject();
        foreach (var (key, value) in map) json[key] = ToNode(value);
        return json;
    }

    public static Dictionary<string, string> StringMap(JsonObject? json)
    {
        var result = new Dictionary<string, string>();
        if (json is null) return result;
        foreach (var (key, value) in json)
        {
            if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } text)
            {
                result[key] = text;
            }
        }
        return result;
    }

    public static DateTimeOffset? Instant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "null") return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    public static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? NonEmpty(JsonObject json, string name) => Text(json, name) is { Length: > 0 } text ? text : null;

    private static bool Flag(JsonObject json, string name) =>
        json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static long? Long(JsonObject json, string name) =>
        json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : null;

    public static OrganizerItem Item(JsonObject json) => new(
        Id: Text(json, "id") ?? throw new JsonException("item without an id"),
        Kind: Text(json, "kind") ?? throw new JsonException("item without a kind"),
        SpaceId: Text(json, "spaceId") ?? "",
        ParentId: NonEmpty(json, "parentId"),
        Deleted: Flag(json, "deleted"),
        Fields: Map(json["fields"] as JsonObject),
        Clocks: StringMap(json["clocks"] as JsonObject),
        Seq: Long(json, "seq"),
        CreatedAt: Instant(Text(json, "createdAt")),
        CreatedBy: NonEmpty(json, "createdBy"),
        UpdatedAt: Instant(Text(json, "updatedAt")),
        UpdatedBy: NonEmpty(json, "updatedBy"));

    public static JsonObject ItemJson(OrganizerItem item) => new()
    {
        ["id"] = item.Id,
        ["kind"] = item.Kind,
        ["spaceId"] = item.SpaceId,
        ["parentId"] = item.ParentId,
        ["deleted"] = item.Deleted,
        ["fields"] = ToNode(item.Fields),
        ["clocks"] = ToNode(item.Clocks),
        ["seq"] = item.Seq,
        ["createdAt"] = item.CreatedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["createdBy"] = item.CreatedBy,
        ["updatedAt"] = item.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["updatedBy"] = item.UpdatedBy,
    };

    public static OrganizerChange Change(JsonObject json) => new(
        Text(json, "id") ?? throw new JsonException("change without an id"),
        Text(json, "kind") ?? throw new JsonException("change without a kind"),
        Map(json["fields"] as JsonObject),
        StringMap(json["clocks"] as JsonObject),
        json["baseClocks"] is JsonObject baseClocks ? StringMap(baseClocks) : null);

    public static JsonObject ChangeJson(OrganizerChange change)
    {
        var json = new JsonObject
        {
            ["id"] = change.Id,
            ["kind"] = change.Kind,
            ["fields"] = ToNode(change.Fields),
            ["clocks"] = ToNode(change.Clocks),
        };
        if (change.BaseClocks is not null) json["baseClocks"] = ToNode(change.BaseClocks);
        return json;
    }

    public static OrganizerSpace Space(JsonObject json) => new(
        Text(json, "id") ?? throw new JsonException("space without an id"),
        Text(json, "kind") switch
        {
            "Personal" => OrganizerSpaceKind.Personal,
            "Group" => OrganizerSpaceKind.Group,
            _ => OrganizerSpaceKind.Family,
        },
        Text(json, "name") ?? "",
        (json["members"] as JsonArray)?.Select(node => node?.GetValue<string>()).OfType<string>().ToList(),
        NonEmpty(json, "color"),
        NonEmpty(json, "createdBy"));

    public static JsonObject SpaceJson(OrganizerSpace space) => new()
    {
        ["id"] = space.Id,
        ["kind"] = space.Kind.ToString(),
        ["name"] = space.Name,
        ["members"] = new JsonArray(space.Members.Select(member => (JsonNode?)JsonValue.Create(member)).ToArray()),
        ["color"] = space.Color,
        ["createdBy"] = space.CreatedBy,
    };

    public static OrganizerStatus Status(JsonObject json) => new(
        Enabled: Flag(json, "enabled"),
        ServerId: Text(json, "serverId") ?? "",
        Username: NonEmpty(json, "username"),
        DisplayName: NonEmpty(json, "displayName"),
        PersonalSpaces: Flag(json, "personalSpaces"),
        ItemCount: Long(json, "itemCount") is { } count ? (int)count : null,
        MaxAttachmentBytes: Long(json, "maxAttachmentBytes"));

    public static JsonObject Parse(byte[] body) =>
        JsonNode.Parse(body) as JsonObject ?? throw new JsonException("expected a JSON object");

    public static string? ErrorMessage(byte[] body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json) return null;
            if (NonEmpty(json, "error") is { } error) return error;
            return json["errors"] is JsonArray errors
                ? string.Join("\n", errors.Select(node => node?.ToString()))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
